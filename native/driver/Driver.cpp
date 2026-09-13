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
    if(correctFramePose) {
        std::lock_guard<std::mutex> lock(historyGate);
        auto frameNow=frameTimeMs();
        if(timeBasedFramePose) {
            physicalLeft=frameHistory.physicalAt(frameNow,prediction);
            physicalRight=frameHistory.physicalAt(frameNow,eyes[1].flHmdPosePredictionTimeInSecondsFromNow);
            match.found=physicalLeft.found && physicalRight.found;
            match.error=-1; // This path does not perform a virtual-pose fit.
        } else {
        match=frameHistory.match(frameNow,prediction,pose);
        // Both eyes must identify the same transform; otherwise forward neither.
        auto right=frameHistory.match(frameNow,eyes[1].flHmdPosePredictionTimeInSecondsFromNow,eyes[1].mHmdPose);
        if(!right.found || (match.found && flight::matrixError(flight::matrix(match.transform),flight::matrix(right.transform))>0.04)) match.found=false;
        }
    }
    if(match.found) {
        std::memcpy(corrected,eyes,sizeof(corrected));
        if(timeBasedFramePose) {
            corrected[0].mHmdPose=flight::matrix(physicalLeft.pose);
            corrected[1].mHmdPose=flight::matrix(physicalRight.pose);
        } else for(auto& eye:corrected) eye.mHmdPose=flight::removeTransform(eye.mHmdPose,match.transform);
        submitOriginal(self,corrected);
    } else submitOriginal(self,eyes);
    submittedLayers.fetch_add(1);
    if(correctFramePose && !match.found) missedLayers.fetch_add(1);
    if(match.held && match.found) heldLayers.fetch_add(1);
    if(log) {
        char message[1400];
        std::snprintf(message,sizeof(message),
            "FrameAudit t=%llu sampled=%d headAge=%lld prediction=%.6f corrected=%d matchError=%.6f timed=%d physicalDt=%.6f layers=%llu missed=%llu held=%llu "
            "layer=[%.6f %.6f %.6f %.6f;%.6f %.6f %.6f %.6f;%.6f %.6f %.6f %.6f] "
            "commandQxyzw=[%.6f %.6f %.6f %.6f] commandP=[%.6f %.6f %.6f] "
            "physicalQxyzw=[%.6f %.6f %.6f %.6f] physicalP=[%.6f %.6f %.6f]",
            static_cast<unsigned long long>(now),sampled?1:0,
            headTime?static_cast<long long>(now)-static_cast<long long>(headTime):-1LL,prediction,match.found?1:0,match.error,timeBasedFramePose?1:0,physicalLeft.predictionSeconds,
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

bool enter() {
    auto result=WaitForSingleObject(ipcMutex,2);
    return result==WAIT_OBJECT_0 || result==WAIT_ABANDONED;
}
void refresh() {
    auto now=GetTickCount64();
    if (state && enter()) {
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
    vr::DriverPose_t output;
    {
        std::lock_guard<std::mutex> lock(gate);
        refresh();
        if(state && device<64 && enter()) {
            auto& sample=state->samples[device];
            sample.pose=flight::physical(input);
            sample.flags=(input.deviceIsConnected?1u:0u)|(input.poseIsValid?2u:0u);
            sample.timestamp=GetTickCount64();
            ReleaseMutex(ipcMutex);
        }
        output=flight::apply(input,command);
        if(device==0 && correctFramePose) {
            std::lock_guard<std::mutex> historyLock(historyGate);
            frameHistory.add(received,output,command,&input);
        }
    }
    originals[index](host,device,output,size);
}
void updated0(void* h,uint32_t d,const vr::DriverPose_t& p,uint32_t n) {updated(0,h,d,p,n);}
void updated1(void* h,uint32_t d,const vr::DriverPose_t& p,uint32_t n) {updated(1,h,d,p,n);}

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
        observeFrames=vr::VRSettings()->GetBool("driver_flugelkranz","observeFrames");
        correctFramePose=vr::VRSettings()->GetBool("driver_flugelkranz","correctFramePose");
        timeBasedFramePose=vr::VRSettings()->GetBool("driver_flugelkranz","timeBasedFramePose");
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
        // A callback may still be executing its original function/trampoline.
        // Stop new detours and let existing callbacks return before freeing it.
        while(inFlight.load()!=0) Sleep(1);
        for(auto& target:addedTargets) if(target) {MH_RemoveHook(target);target=nullptr;}
        if(componentTarget) {MH_RemoveHook(componentTarget);componentTarget=nullptr;}
        if(submitTarget) {MH_RemoveHook(submitTarget);submitTarget=nullptr;}
        for(auto& target:targets) if(target) {MH_RemoveHook(target);target=nullptr;}
        std::lock_guard<std::mutex> lock(gate);
        if(state) {UnmapViewOfFile(state);state=nullptr;}
        if(mapping) {CloseHandle(mapping);mapping=nullptr;}
        if(ipcMutex) {CloseHandle(ipcMutex);ipcMutex=nullptr;}
        if(minHookInitialized) {MH_Uninitialize();minHookInitialized=false;}
        command={};lastHeartbeat=0;
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
