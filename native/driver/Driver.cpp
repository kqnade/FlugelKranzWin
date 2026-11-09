#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <MinHook.h>
#include <mutex>
#include <atomic>
#include <cstring>
#include <new>
#include <cstdio>
#include <chrono>
#include <array>
#include <intrin.h>
#include "Transform.h"
#include "FrameHistory.h"

namespace {
using Callback=void(*)(void*,uint32_t,const vr::DriverPose_t&,uint32_t);
Callback originals[2]{};
void* targets[2]{};
HANDLE mapping=nullptr,ipcMutex=nullptr;
flight::State* state=nullptr;
std::mutex gate;
std::atomic<unsigned> inFlight{0};
struct CallbackScope {
    CallbackScope(){inFlight.fetch_add(1);}
    ~CallbackScope(){inFlight.fetch_sub(1);}
};
flight::Pose command;
uint64_t lastHeartbeat=0;
// Direct Mode diagnostics and opt-in render-pose correction. Textures are untouched.
using Added=bool(*)(void*,const char*,vr::ETrackedDeviceClass,vr::ITrackedDeviceServerDriver*);
using Component=void*(*)(void*,const char*);
using Layer=vr::IVRDriverDirectModeComponent::SubmitLayerPerEye_t;
using Submit=void(*)(void*,const Layer(&)[2]);
Added addedOriginal[2]{};
void* addedTargets[2]{};
Component componentOriginal=nullptr;
Submit submitOriginal=nullptr;
void* componentTarget=nullptr;
void* submitTarget=nullptr;
std::mutex observerGate;
std::atomic<uint64_t> nextFrameLog{0};
std::atomic<uint64_t> submittedLayers{0},missedLayers{0},heldLayers{0};
bool observeFrames=false;
bool correctFramePose=false;
bool timeBasedFramePose=false;
flight::FrameHistory frameHistory;
std::mutex historyGate;
double frameTimeMs() {
    return std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
bool observerStopping=false; // protected by observerGate
// SlimeVR-style solvers build their skeleton around the HMD pose they read back
// from SteamVR, so their tracker poses already contain the flight translation.
// 0 = not classified yet, 1 = own tracking, 2 = anchored to the flown HMD.
std::array<std::atomic<uint8_t>,64> anchoredDevices{};
char hmdAnchoredSystems[256]="";
std::atomic<bool> propertiesReady{false};
double physicalHead[3]{}; // protected by gate
// Solvers that read raw poses back from inside vrserver (SlimeVR and rebocap read the
// HMD this way) must see the physical body. Otherwise they rebuild the skeleton around
// the flown HMD and the flight lands on their trackers a second time.
using RawPoses=void(*)(void*,float,vr::TrackedDevicePose_t*,uint32_t);
RawPoses rawOriginal=nullptr;
void* rawTarget=nullptr;
char physicalPoseDrivers[256]="driver_slimevr.dll,driver_rebocap.dll";
// appliedGate is held only for a few assignments before and after a pose update is
// forwarded and for one array copy by the readback hook. The readback must not take
// `gate`: SlimeVR polls raw poses every ~2 ms, pose updates hold `gate`, and the HMD's
// own updates then queue behind them. Measured on Quest 2 + VD + SlimeVR, that left the
// render correction using HMD samples 47 ms or older in 72% of flying frames
// (0.3% before the hook), which is visible as judder in the headset.
std::mutex appliedGate;
std::array<flight::Pose,64> lastApplied{}; // protected by appliedGate
// While updated() forwards a pose, the readback cannot tell whether vrserver already holds
// it, so it also gets the transform that pose carries and its physical position.
std::array<flight::Pose,64> pendingApplied{}; // protected by appliedGate
std::array<std::array<double,3>,64> pendingPhysical{}; // protected by appliedGate
std::array<bool,64> forwarding{}; // protected by appliedGate
struct Forward {uint64_t generation=0;flight::Pose transform,body;};
constexpr size_t forwardRing=8;
std::array<std::array<Forward,forwardRing>,64> recentForwards{}; // protected by appliedGate
std::array<uint64_t,64> startedGeneration{}; // protected by appliedGate
std::array<std::atomic<uint64_t>,64> committedGeneration{}; // written under appliedGate, read lock-free
std::array<uint32_t,64> forwardsInFlight{}; // protected by appliedGate
std::array<bool,64> reordered{}; // protected by appliedGate
// Call sites are classified once each and cached lock-free, so the hot path takes
// neither a mutex nor the loader lock.
std::array<std::atomic<void*>,8> physicalCallSites{};
std::array<std::atomic<void*>,8> flownCallSites{};
std::mutex callSiteGate; // only while classifying a call site seen for the first time

bool servesPhysicalPoses(void* returnAddress) {
    for(const auto& site:physicalCallSites) if(site.load(std::memory_order_relaxed)==returnAddress) return true;
    for(const auto& site:flownCallSites) if(site.load(std::memory_order_relaxed)==returnAddress) return false;
    std::lock_guard<std::mutex> lock(callSiteGate);
    char name[MAX_PATH]{};
    HMODULE module=nullptr;
    if(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        static_cast<LPCWSTR>(returnAddress),&module)) {
        wchar_t path[MAX_PATH]{};
        const DWORD length=GetModuleFileNameW(module,path,MAX_PATH);
        const wchar_t* file=path;
        for(DWORD i=0;i<length;i++) if(path[i]==L'\\' || path[i]==L'/') file=path+i+1;
        WideCharToMultiByte(CP_UTF8,0,file,-1,name,sizeof(name),nullptr,nullptr);
    }
    const bool physical=name[0] && flight::listContains(physicalPoseDrivers,name);
    for(auto& site:physical?physicalCallSites:flownCallSites) {
        if(site.load()==returnAddress) break;
        void* expected=nullptr;
        if(site.compare_exchange_strong(expected,returnAddress)) {
            char message[MAX_PATH+96];
            std::snprintf(message,sizeof(message),"FlugelKranz: raw poses for %s: %s",name[0]?name:"(unknown module)",physical?"physical (flight removed)":"flown");
            vr::VRDriverLog()->Log(message);
            break;
        }
    }
    return physical;
}

struct ReadbackTally {uint64_t window=0,pickedPending=0,pickedPrevious=0,ambiguous=0,overflow=0;double residualMax=0;};
void tallyPick(ReadbackTally& tally,const flight::ReadbackPick& pick,bool different) {
    tally.window++;
    (pick.pending?tally.pickedPending:tally.pickedPrevious)++;
    // Two different candidates score within a factor of two: the pick is a guess.
    if(different && pick.farther<2*pick.nearer) tally.ambiguous++;
    tally.residualMax=std::max(tally.residualMax,pick.residual);
}
// Pair readbacks with the generation observed before entering vrserver. A device whose committed generation is the same as before the
// call, with no forward in flight and no out-of-order finish, gets the committed transform:
// nothing reached vrserver in between. Any other device gets the best-scoring forward from
// the one committed before the call to the newest started.
void readbackByGeneration(vr::TrackedDevicePose_t* poses,uint32_t count,const std::array<uint64_t,64>& committedBefore,ReadbackTally& tally) {
    struct Unsettled {uint32_t device=0;size_t count=0;bool different=false;std::array<flight::ReadbackCandidate,forwardRing+1> candidates{};};
    std::array<flight::Pose,64> applied;
    std::array<Unsettled,64> unsettled; // every tracked device can change during a readback
    size_t unsettledCount=0;
    const uint32_t devices=std::min<uint32_t>(count,64);
    {
        std::lock_guard<std::mutex> lock(appliedGate);
        for(uint32_t device=0;device<devices;device++) {
            applied[device]=lastApplied[device];
            const uint64_t committed=committedGeneration[device].load(std::memory_order_relaxed);
            if(committed==committedBefore[device] && !forwardsInFlight[device] && !reordered[device]) continue;
            if(unsettledCount==unsettled.size()) {tally.overflow++;continue;}
            auto& entry=unsettled[unsettledCount++];
            entry.device=device;entry.count=0;
            const uint64_t started=startedGeneration[device];
            uint64_t from=committedBefore[device];
            // Overlapping forwards of one device can reach vrserver in either order, so an
            // older one than the committed may be what it holds: consider every recent one.
            if(reordered[device] || forwardsInFlight[device]>0) from=started>=forwardRing?started-forwardRing+1:0;
            else if(started-from>=forwardRing) {from=started-forwardRing+1;tally.overflow++;} // the oldest are gone
            for(uint64_t generation=from;generation<=started;generation++) {
                if(generation==0) {entry.candidates[entry.count++]={flight::Pose{},flight::Pose{}};continue;} // before any forward
                const auto& forward=recentForwards[device][generation%forwardRing];
                if(forward.generation==generation) entry.candidates[entry.count++]={forward.transform,forward.body};
            }
            // Generation 0 has no body of its own; the body hardly moves between updates.
            if(entry.count>1 && from==0) entry.candidates[0].body=entry.candidates[1].body;
            if(!entry.count || (entry.count==1 && from==0)) entry.count=0; // nothing to score against
        }
    }
    std::array<bool,64> settled;settled.fill(true);
    for(size_t i=0;i<unsettledCount;i++) {
        auto& entry=unsettled[i];
        if(!entry.count) continue;
        settled[entry.device]=false;
        const auto pick=flight::pickReadback(poses[entry.device].mDeviceToAbsoluteTracking,entry.candidates.data(),entry.count);
        tallyPick(tally,pick,pick.farther>0);
        flight::removeTransform(poses[entry.device],pick.transform);
    }
    for(uint32_t device=0;device<devices;device++) if(settled[device]) flight::removeTransform(poses[device],applied[device]);
}
void rawPoses(void* host,float prediction,vr::TrackedDevicePose_t* poses,uint32_t count) {
    CallbackScope scope;
    const auto caller=_ReturnAddress();
    std::array<uint64_t,64> committedBefore;
    for(uint32_t device=0;device<64;device++) committedBefore[device]=committedGeneration[device].load(std::memory_order_acquire);
    rawOriginal(host,prediction,poses,count);
    if(!poses || !count || !servesPhysicalPoses(caller)) return;
    ReadbackTally tally;
    readbackByGeneration(poses,count,committedBefore,tally);
}
bool physicalHeadValid=false; // protected by gate

uint8_t classify(uint32_t device) {
    if(device>=anchoredDevices.size()) return 1;
    const uint8_t known=anchoredDevices[device].load();
    if(known) return known;
    if(!propertiesReady.load()) return 1;
    vr::ETrackedPropertyError error=vr::TrackedProp_Success;
    auto& properties=*vr::VRProperties();
    auto system=properties.GetStringProperty(properties.TrackedDeviceToPropertyContainer(device),vr::Prop_TrackingSystemName_String,&error);
    // A driver may submit a pose before its properties are readable; ask again next time.
    if(error==vr::TrackedProp_NotYetAvailable || error==vr::TrackedProp_InvalidDevice) return 1;
    const uint8_t result=error==vr::TrackedProp_Success && flight::listContains(hmdAnchoredSystems,system.c_str())?2:1;
    anchoredDevices[device].store(result);
    if(result==2) {
        char message[256];
        std::snprintf(message,sizeof(message),"FlugelKranz: device %u (%s) follows the flown HMD; applying rotation about it only",device,system.c_str());
        vr::VRDriverLog()->Log(message);
    }
    return result;
}

void observedSubmit(void* self,const Layer(&eyes)[2]) {
    CallbackScope scope;
    // Textures/projection/timing stay untouched. Only matched render pose metadata
    // is copied and converted back to physical space for the streaming driver.
    auto pose=eyes[0].mHmdPose;
    auto prediction=eyes[0].flHmdPosePredictionTimeInSecondsFromNow;
    auto now=GetTickCount64();
    auto next=nextFrameLog.load();
    bool log=now>=next && nextFrameLog.compare_exchange_strong(next,now+1000);
    flight::Pose sampledCommand,head;
    uint64_t headTime=0;
    bool sampled=false;
    if(log && gate.try_lock()) {
        sampledCommand=command;
        auto acquired=state?WaitForSingleObject(ipcMutex,0):WAIT_TIMEOUT;
        if(acquired==WAIT_OBJECT_0 || acquired==WAIT_ABANDONED) {
            head=state->samples[0].pose;headTime=state->samples[0].timestamp;
            ReleaseMutex(ipcMutex);sampled=true;
        }
        gate.unlock();
    }
    flight::FrameMatch match;
    flight::PhysicalFrame physicalLeft,physicalRight;
    Layer corrected[2];
    bool identityBypass=false,usedTimed=false;
    if(correctFramePose) {
        std::lock_guard<std::mutex> lock(historyGate);
        auto frameNow=frameTimeMs();
        identityBypass=frameHistory.canBypassCorrection(frameNow);
        if(identityBypass) {
            // Forward the original layer below without copying or predicting it.
        } else if(timeBasedFramePose) {
            match=frameHistory.heldMatch(frameNow,prediction,pose);
            auto right=frameHistory.heldMatch(frameNow,eyes[1].flHmdPosePredictionTimeInSecondsFromNow,eyes[1].mHmdPose);
            if(!right.found) match.found=false;
            if(!match.found) {
            usedTimed=true;
            physicalLeft=frameHistory.physicalAt(frameNow,prediction);
            physicalRight=frameHistory.physicalAt(frameNow,eyes[1].flHmdPosePredictionTimeInSecondsFromNow);
            match.found=physicalLeft.found && physicalRight.found;
            match.error=-1; // This path does not perform a virtual-pose fit.
            match.held=false;
            }
        } else {
        match=frameHistory.match(frameNow,prediction,pose);
        // Both eyes must identify the same transform; otherwise forward neither.
        auto right=frameHistory.match(frameNow,eyes[1].flHmdPosePredictionTimeInSecondsFromNow,eyes[1].mHmdPose);
        if(!right.found || (match.found && flight::matrixError(flight::matrix(match.transform),flight::matrix(right.transform))>0.04)) match.found=false;
        }
    }
    if(match.found) {
        std::memcpy(corrected,eyes,sizeof(corrected));
        if(usedTimed) {
            corrected[0].mHmdPose=flight::matrix(physicalLeft.pose);
            corrected[1].mHmdPose=flight::matrix(physicalRight.pose);
        } else for(auto& eye:corrected) eye.mHmdPose=flight::removeTransform(eye.mHmdPose,match.transform);
        submitOriginal(self,corrected);
    } else submitOriginal(self,eyes);
    submittedLayers.fetch_add(1);
    if(correctFramePose && !identityBypass && !match.found) missedLayers.fetch_add(1);
    if(match.held && match.found) heldLayers.fetch_add(1);
    if(log) {
        char message[1400];
        std::snprintf(message,sizeof(message),
            "FrameAudit t=%llu sampled=%d headAge=%lld prediction=%.6f corrected=%d matchError=%.6f timed=%d identityBypass=%d physicalDt=%.6f layers=%llu missed=%llu held=%llu "
            "layer=[%.6f %.6f %.6f %.6f;%.6f %.6f %.6f %.6f;%.6f %.6f %.6f %.6f] "
            "commandQxyzw=[%.6f %.6f %.6f %.6f] commandP=[%.6f %.6f %.6f] "
            "physicalQxyzw=[%.6f %.6f %.6f %.6f] physicalP=[%.6f %.6f %.6f]",
            static_cast<unsigned long long>(now),sampled?1:0,
            headTime?static_cast<long long>(now)-static_cast<long long>(headTime):-1LL,prediction,match.found?1:0,match.error,usedTimed?1:0,identityBypass?1:0,physicalLeft.predictionSeconds,
            static_cast<unsigned long long>(submittedLayers.exchange(0)),
            static_cast<unsigned long long>(missedLayers.exchange(0)),
            static_cast<unsigned long long>(heldLayers.exchange(0)),
            pose.m[0][0],pose.m[0][1],pose.m[0][2],pose.m[0][3],
            pose.m[1][0],pose.m[1][1],pose.m[1][2],pose.m[1][3],
            pose.m[2][0],pose.m[2][1],pose.m[2][2],pose.m[2][3],
            sampledCommand.x,sampledCommand.y,sampledCommand.z,sampledCommand.w,
            sampledCommand.px,sampledCommand.py,sampledCommand.pz,
            head.x,head.y,head.z,head.w,head.px,head.py,head.pz);
        vr::VRDriverLog()->Log(message);
    }
}

void* observedComponent(void* self,const char* name) {
    CallbackScope scope;
    auto result=componentOriginal(self,name);
    if(!result || !name || std::strcmp(name,vr::IVRDriverDirectModeComponent_Version)!=0) return result;
    std::lock_guard<std::mutex> lock(observerGate);
    if(!observerStopping && !submitTarget) {
        auto target=(*reinterpret_cast<void***>(result))[4];
        auto status=MH_CreateHook(target,reinterpret_cast<void*>(observedSubmit),reinterpret_cast<void**>(&submitOriginal));
        if(status==MH_OK) {
            submitTarget=target;
            status=MH_EnableHook(target);
        }
        vr::VRDriverLog()->Log(status==MH_OK?(correctFramePose?"FrameAudit: DirectMode_009 history-matched pose correction installed":"FrameAudit: DirectMode_009 SubmitLayer observer installed (no writes)"):"FrameAudit: SubmitLayer hook failed");
    }
    return result;
}

bool observedAdded(int index,void* host,const char* serial,vr::ETrackedDeviceClass type,vr::ITrackedDeviceServerDriver* driver) {
    CallbackScope scope;
    if(type==vr::TrackedDeviceClass_HMD && driver) {
        std::lock_guard<std::mutex> lock(observerGate);
        if(!observerStopping && !componentTarget) {
            auto target=(*reinterpret_cast<void***>(driver))[3];
            auto status=MH_CreateHook(target,reinterpret_cast<void*>(observedComponent),reinterpret_cast<void**>(&componentOriginal));
            if(status==MH_OK) {
                componentTarget=target;
                status=MH_EnableHook(target);
            }
            vr::VRDriverLog()->Log(status==MH_OK?"FrameAudit: HMD GetComponent observer installed":"FrameAudit: HMD GetComponent observer failed");
        }
    }
    return addedOriginal[index](host,serial,type,driver);
}
bool added0(void* h,const char* s,vr::ETrackedDeviceClass c,vr::ITrackedDeviceServerDriver* d) {return observedAdded(0,h,s,c,d);}
bool added1(void* h,const char* s,vr::ETrackedDeviceClass c,vr::ITrackedDeviceServerDriver* d) {return observedAdded(1,h,s,c,d);}

bool enter(DWORD timeout=2) {
    auto result=WaitForSingleObject(ipcMutex,timeout);
    return result==WAIT_OBJECT_0 || result==WAIT_ABANDONED;
}
// Pose updates and RunFrame hold `gate`, so they only try the IPC mutex. Waiting for it
// would stall every device's pose update whenever the app is paused while holding it
// (a .NET GC, for example). A miss keeps the previous command and skips one sample.
void refresh() {
    auto now=GetTickCount64();
    if (state && enter(0)) {
        // Sample after acquisition: the client can publish while this thread is descheduled.
        now=GetTickCount64();
        state->driverHeartbeat=now;
        if(state->owner && (now<state->clientHeartbeat || now-state->clientHeartbeat>=500)) {
            state->owner=0;state->enabled=0;
        }
        if(state->owner && state->enabled && now>=state->clientHeartbeat
            && now-state->clientHeartbeat<500 && flight::valid(state->command)) {
            command=state->command; lastHeartbeat=state->clientHeartbeat;
        } else { command={}; lastHeartbeat=0; }
        ReleaseMutex(ipcMutex);
    }
    if(!lastHeartbeat || now-lastHeartbeat>=500) command={};
}
void updated(int index,void* host,uint32_t device,const vr::DriverPose_t& input,uint32_t size) {
    CallbackScope scope;
    auto received=frameTimeMs();
    // Never inspect an unknown ABI-sized pose.
    if(size!=sizeof(vr::DriverPose_t)) { originals[index](host,device,input,size); return; }
    const bool anchored=device!=0 && classify(device)==2;
    vr::DriverPose_t output;
    flight::Pose transform;
    uint64_t generation=0;
    {
        std::lock_guard<std::mutex> lock(gate);
        refresh();
        if(state && device<64 && enter(0)) {
            auto& sample=state->samples[device];
            sample.pose=flight::physical(input);
            sample.flags=(input.deviceIsConnected?1u:0u)|(input.poseIsValid?2u:0u);
            sample.timestamp=GetTickCount64();
            ReleaseMutex(ipcMutex);
        }
        if(device==0 && input.poseIsValid && input.deviceIsConnected) {
            auto head=flight::physical(input);
            if(flight::valid(head)) {physicalHead[0]=head.px;physicalHead[1]=head.py;physicalHead[2]=head.pz;physicalHeadValid=true;}
        }
        transform=command;
        if(anchored) {
            // The solver already moved these trackers with the flown HMD it read back.
            // Rotate them about where that HMD is now instead of moving them again.
            double flownHead[3];
            flight::transformPoint(command,physicalHead,flownHead);
            transform=physicalHeadValid?flight::aboutPivot(command,flownHead):flight::Pose{};
        }
        output=flight::apply(input,transform);
        if(device<lastApplied.size()) {
            const auto body=flight::physical(input);
            std::lock_guard<std::mutex> applied(appliedGate);
            pendingApplied[device]=transform;
            pendingPhysical[device]={body.px,body.py,body.pz};
            forwarding[device]=true;
            generation=++startedGeneration[device];
            recentForwards[device][generation%forwardRing]={generation,transform,body};
            forwardsInFlight[device]++;
        }
        if(device==0 && correctFramePose) {
            std::lock_guard<std::mutex> historyLock(historyGate);
            frameHistory.add(received,output,command,&input);
        }
    }
    originals[index](host,device,output,size);
    if(device<lastApplied.size()) {
        std::lock_guard<std::mutex> applied(appliedGate);
        forwardsInFlight[device]--;
        if(generation>committedGeneration[device].load(std::memory_order_relaxed)) {
            committedGeneration[device].store(generation,std::memory_order_release);
            reordered[device]=false;
            lastApplied[device]=transform;
        } else reordered[device]=true;
        forwarding[device]=false;
    }
}
void updated0(void* h,uint32_t d,const vr::DriverPose_t& p,uint32_t n) {updated(0,h,d,p,n);}
void updated1(void* h,uint32_t d,const vr::DriverPose_t& p,uint32_t n) {updated(1,h,d,p,n);}

// A flag missing from every settings file keeps its compiled fallback. The fallback is
// what the driver did before the flag existed, so an old default.vrsettings next to a
// new dll changes nothing.
bool readFlag(const char* key,bool fallback) {
    vr::EVRSettingsError error=vr::VRSettingsError_None;
    const bool value=vr::VRSettings()->GetBool("driver_flugelkranz",key,&error);
    return error==vr::VRSettingsError_None?value:fallback;
}
// Read once from Init, before any hook is installed.
void loadSettings() {
    observeFrames=readFlag("observeFrames",false);
    correctFramePose=readFlag("correctFramePose",false);
    timeBasedFramePose=readFlag("timeBasedFramePose",false);
    char configured[sizeof(hmdAnchoredSystems)]{};
    vr::EVRSettingsError settingsError=vr::VRSettingsError_None;
    vr::VRSettings()->GetString("driver_flugelkranz","hmdAnchoredTrackingSystems",configured,sizeof(configured),&settingsError);
    if(settingsError==vr::VRSettingsError_None) std::memcpy(hmdAnchoredSystems,configured,sizeof(configured));
    char message[320];
    std::snprintf(message,sizeof(message),"FlugelKranz: HMD-anchored tracking systems: '%s'",hmdAnchoredSystems);
    vr::VRDriverLog()->Log(message);
    char drivers[sizeof(physicalPoseDrivers)]{};
    settingsError=vr::VRSettingsError_None;
    vr::VRSettings()->GetString("driver_flugelkranz","physicalPoseDrivers",drivers,sizeof(drivers),&settingsError);
    if(settingsError==vr::VRSettingsError_None) std::memcpy(physicalPoseDrivers,drivers,sizeof(drivers));
    std::snprintf(message,sizeof(message),"FlugelKranz: physical raw poses for drivers: '%s'",physicalPoseDrivers);
    vr::VRDriverLog()->Log(message);
    // One line with every behaviour flag, so a log always says which paths were live.
    std::snprintf(message,sizeof(message),"FlugelKranz flags: observeFrames=%d correctFramePose=%d timeBasedFramePose=%d",
        observeFrames?1:0,correctFramePose?1:0,timeBasedFramePose?1:0);
    vr::VRDriverLog()->Log(message);
}

class Provider final : public vr::IServerTrackedDeviceProvider {
    bool minHookInitialized=false;
public:
    vr::EVRInitError Init(vr::IVRDriverContext* context) override {
        VR_INIT_SERVER_DRIVER_CONTEXT(context);
        ipcMutex=CreateMutexW(nullptr,FALSE,L"Local\\FlugelKranz.Driver.Mutex.v1");
        mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(flight::State),L"Local\\FlugelKranz.Driver.State.v1");
        if(!mapping || !ipcMutex) {Cleanup();return vr::VRInitError_Driver_Failed;}
        state=static_cast<flight::State*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(flight::State)));
        if(!state || !enter()) {Cleanup();return vr::VRInitError_Driver_Failed;}
        new(state) flight::State{};
        state->driverHeartbeat=GetTickCount64();
        ReleaseMutex(ipcMutex);
        auto result=MH_Initialize();
        if(result!=MH_OK) {Cleanup();return vr::VRInitError_Driver_Failed;}
        minHookInitialized=true;
        observerStopping=false;
        loadSettings();
        propertiesReady=true;
        const char* versions[]={"IVRServerDriverHost_006","IVRServerDriverHost_005"};
        void* detours[]={reinterpret_cast<void*>(updated0),reinterpret_cast<void*>(updated1)};
        void* addedDetours[]={reinterpret_cast<void*>(added0),reinterpret_cast<void*>(added1)};
        int installed=0;
        for(int i=0;i<2;i++) {
            vr::EVRInitError error=vr::VRInitError_None;
            auto host=context->GetGenericInterface(versions[i],&error);
            if(!host || error!=vr::VRInitError_None) continue;
            if(observeFrames || correctFramePose) {
                auto addedTarget=(*reinterpret_cast<void***>(host))[0];
                if(i==0 || addedTarget!=addedTargets[0]) {
                    if(MH_CreateHook(addedTarget,addedDetours[i],reinterpret_cast<void**>(&addedOriginal[i]))!=MH_OK) {Cleanup();return vr::VRInitError_Driver_Failed;}
                    addedTargets[i]=addedTarget;
                    if(MH_EnableHook(addedTarget)!=MH_OK) {Cleanup();return vr::VRInitError_Driver_Failed;}
                }
            }
            auto target=(*reinterpret_cast<void***>(host))[1];
            if(i==1 && target==targets[0]) continue;
            if(MH_CreateHook(target,detours[i],reinterpret_cast<void**>(&originals[i]))!=MH_OK) {Cleanup();return vr::VRInitError_Driver_Failed;}
            targets[i]=target;
            if(MH_EnableHook(target)!=MH_OK) {Cleanup();return vr::VRInitError_Driver_Failed;}
            installed++;
        }
        if(!installed) {Cleanup();return vr::VRInitError_Driver_Failed;}
        if(physicalPoseDrivers[0]) {
            vr::EVRInitError hostError=vr::VRInitError_None;
            auto host=context->GetGenericInterface("IVRServerDriverHost_006",&hostError);
            auto target=host && hostError==vr::VRInitError_None?(*reinterpret_cast<void***>(host))[6]:nullptr;
            if(target && MH_CreateHook(target,reinterpret_cast<void*>(rawPoses),reinterpret_cast<void**>(&rawOriginal))==MH_OK) {
                rawTarget=target;
                vr::VRDriverLog()->Log(MH_EnableHook(target)==MH_OK
                    ?"FlugelKranz: raw pose readback hook installed"
                    :"FlugelKranz: raw pose readback hook could not be enabled");
            } else vr::VRDriverLog()->Log("FlugelKranz: raw pose readback hook failed; HMD-based solvers will see the flown HMD");
        }
        vr::VRDriverLog()->Log("FlugelKranz driver ready: raw snapshots + XYZ body-pose transforms v2");
        return vr::VRInitError_None;
    }
    void Cleanup() override {
        // Finish any installation before disabling targets. A callback already
        // inside GetComponent must not install a fresh hook during shutdown.
        {
            std::lock_guard<std::mutex> lock(observerGate);
            observerStopping=true;
        }
        for(auto target:addedTargets) if(target) MH_DisableHook(target);
        if(componentTarget) MH_DisableHook(componentTarget);
        if(submitTarget) MH_DisableHook(submitTarget);
        for(auto target:targets) if(target) MH_DisableHook(target);
        if(rawTarget) MH_DisableHook(rawTarget);
        // A callback may still be executing its original function/trampoline.
        // Stop new detours and let existing callbacks return before freeing it.
        while(inFlight.load()!=0) Sleep(1);
        for(auto& target:addedTargets) if(target) {MH_RemoveHook(target);target=nullptr;}
        if(componentTarget) {MH_RemoveHook(componentTarget);componentTarget=nullptr;}
        if(submitTarget) {MH_RemoveHook(submitTarget);submitTarget=nullptr;}
        for(auto& target:targets) if(target) {MH_RemoveHook(target);target=nullptr;}
        if(rawTarget) {MH_RemoveHook(rawTarget);rawTarget=nullptr;}
        std::lock_guard<std::mutex> lock(gate);
        if(state) {UnmapViewOfFile(state);state=nullptr;}
        if(mapping) {CloseHandle(mapping);mapping=nullptr;}
        if(ipcMutex) {CloseHandle(ipcMutex);ipcMutex=nullptr;}
        if(minHookInitialized) {MH_Uninitialize();minHookInitialized=false;}
        command={};lastHeartbeat=0;
        physicalHeadValid=false;
        for(auto& known:anchoredDevices) known.store(0);
        {
            std::lock_guard<std::mutex> applied(appliedGate);
            lastApplied.fill(flight::Pose{});pendingApplied.fill(flight::Pose{});
            pendingPhysical.fill({});forwarding.fill(false);
            for(auto& forwards:recentForwards) forwards.fill(Forward{});
            startedGeneration.fill(0);forwardsInFlight.fill(0);reordered.fill(false);
            for(auto& committed:committedGeneration) committed.store(0);
        }
        for(auto& site:physicalCallSites) site.store(nullptr);
        for(auto& site:flownCallSites) site.store(nullptr);
        propertiesReady=false;
        frameHistory.clear();
        VR_CLEANUP_SERVER_DRIVER_CONTEXT();
    }
    const char* const* GetInterfaceVersions() override {return vr::k_InterfaceVersions;}
    void RunFrame() override {std::lock_guard<std::mutex> lock(gate);refresh();}
    bool ShouldBlockStandbyMode() override {return false;}
    void EnterStandby() override {}
    void LeaveStandby() override {}
} provider;
}
extern "C" __declspec(dllexport) void* HmdDriverFactory(const char* name,int* error) {
    if(name && std::strcmp(name,vr::IServerTrackedDeviceProvider_Version)==0) {
        if(error)*error=vr::VRInitError_None;
        return &provider;
    }
    if(error)*error=vr::VRInitError_Init_InterfaceNotFound;
    return nullptr;
}
