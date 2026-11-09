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
#include "PoseReadbackHistory.h"
#include "DriverIpc.h"
#include "PhysicalPoseCallerPolicy.h"
#include "FrameCorrection.h"
#include "HookRegistration.h"

namespace {
flight::PoseReadbackHistory readback;
flight::DriverIpc ipc;
flight::HookRegistration hooks;
flight::FrameCorrection frameCorrection;
using flight::frameTimeMs;
flight::PhysicalPoseCallerPolicy callerPolicy;
std::mutex gate;
using Layer=flight::HookRegistration::Layer;
using CallbackScope=flight::HookRegistration::CallbackScope;
std::atomic<uint64_t> nextFrameLog{0};
std::atomic<uint64_t> submittedLayers{0},missedLayers{0},heldLayers{0};
bool observeFrames=false;
double physicalHead[3]{}; // protected by gate
// Solvers that read raw poses back from inside vrserver (SlimeVR and rebocap read the
// HMD this way) must see the physical body. Otherwise they rebuild the skeleton around
// the flown HMD and the flight lands on their trackers a second time.
void rawPoses(void* host,float prediction,vr::TrackedDevicePose_t* poses,uint32_t count) {
    CallbackScope scope{hooks};
    const auto caller=_ReturnAddress();
    const auto committedBefore=readback.CaptureCommitted();
    hooks.rawOriginal(host,prediction,poses,count);
    if(!poses || !count || !callerPolicy.servesPhysicalPoses(caller)) return;
    flight::PoseReadbackHistory::ReadbackTally tally;
    readback.readbackByGeneration(poses,count,committedBefore,tally);
}
bool physicalHeadValid=false; // protected by gate

void observedSubmit(void* self,const Layer(&eyes)[2]) {
    CallbackScope scope{hooks};
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
        sampledCommand=ipc.command;
        auto acquired=ipc.state?WaitForSingleObject(ipc.ipcMutex,0):WAIT_TIMEOUT;
        if(acquired==WAIT_OBJECT_0 || acquired==WAIT_ABANDONED) {
            head=ipc.state->samples[0].pose;headTime=ipc.state->samples[0].timestamp;
            ReleaseMutex(ipc.ipcMutex);sampled=true;
        }
        gate.unlock();
    }
    Layer corrected[2];
    const auto correction=frameCorrection.Prepare(eyes,corrected);
    const auto& match=correction.match;
    const auto& physicalLeft=correction.physicalLeft;
    const bool identityBypass=correction.identityBypass,usedTimed=correction.usedTimed;
    if(match.found) hooks.submitOriginal(self,corrected);
    else hooks.submitOriginal(self,eyes);
    submittedLayers.fetch_add(1);
    if(frameCorrection.correctFramePose && !identityBypass && !match.found) missedLayers.fetch_add(1);
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
    CallbackScope scope{hooks};
    auto result=hooks.componentOriginal(self,name);
    if(!result || !name || std::strcmp(name,vr::IVRDriverDirectModeComponent_Version)!=0) return result;
    std::lock_guard<std::mutex> lock(hooks.observerGate);
    if(!hooks.observerStopping && !hooks.submitTarget) {
        auto target=(*reinterpret_cast<void***>(result))[4];
        auto status=MH_CreateHook(target,reinterpret_cast<void*>(observedSubmit),reinterpret_cast<void**>(&hooks.submitOriginal));
        if(status==MH_OK) {
            hooks.submitTarget=target;
            status=MH_EnableHook(target);
        }
        vr::VRDriverLog()->Log(status==MH_OK?(frameCorrection.correctFramePose?"FrameAudit: DirectMode_009 history-matched pose correction installed":"FrameAudit: DirectMode_009 SubmitLayer observer installed (no writes)"):"FrameAudit: SubmitLayer hook failed");
    }
    return result;
}

bool observedAdded(int index,void* host,const char* serial,vr::ETrackedDeviceClass type,vr::ITrackedDeviceServerDriver* driver) {
    CallbackScope scope{hooks};
    if(type==vr::TrackedDeviceClass_HMD && driver) {
        std::lock_guard<std::mutex> lock(hooks.observerGate);
        if(!hooks.observerStopping && !hooks.componentTarget) {
            auto target=(*reinterpret_cast<void***>(driver))[3];
            auto status=MH_CreateHook(target,reinterpret_cast<void*>(observedComponent),reinterpret_cast<void**>(&hooks.componentOriginal));
            if(status==MH_OK) {
                hooks.componentTarget=target;
                status=MH_EnableHook(target);
            }
            vr::VRDriverLog()->Log(status==MH_OK?"FrameAudit: HMD GetComponent observer installed":"FrameAudit: HMD GetComponent observer failed");
        }
    }
    return hooks.addedOriginal[index](host,serial,type,driver);
}
bool added0(void* h,const char* s,vr::ETrackedDeviceClass c,vr::ITrackedDeviceServerDriver* d) {return observedAdded(0,h,s,c,d);}
bool added1(void* h,const char* s,vr::ETrackedDeviceClass c,vr::ITrackedDeviceServerDriver* d) {return observedAdded(1,h,s,c,d);}

void updated(int index,void* host,uint32_t device,const vr::DriverPose_t& input,uint32_t size) {
    CallbackScope scope{hooks};
    auto received=frameTimeMs();
    // Never inspect an unknown ABI-sized pose.
    if(size!=sizeof(vr::DriverPose_t)) { hooks.originals[index](host,device,input,size); return; }
    const bool anchored=device!=0 && callerPolicy.classify(device)==2;
    vr::DriverPose_t output;
    flight::Pose transform;
    uint64_t generation=0;
    {
        std::lock_guard<std::mutex> lock(gate);
        ipc.refresh();
        ipc.PublishSample(device,input);
        if(device==0 && input.poseIsValid && input.deviceIsConnected) {
            auto head=flight::physical(input);
            if(flight::valid(head)) {physicalHead[0]=head.px;physicalHead[1]=head.py;physicalHead[2]=head.pz;physicalHeadValid=true;}
        }
        transform=ipc.command;
        if(anchored) {
            // The solver already moved these trackers with the flown HMD it read back.
            // Rotate them about where that HMD is now instead of moving them again.
            double flownHead[3];
            flight::transformPoint(ipc.command,physicalHead,flownHead);
            transform=physicalHeadValid?flight::aboutPivot(ipc.command,flownHead):flight::Pose{};
        }
        output=flight::apply(input,transform);
        if(device<64) generation=readback.BeginForward(device,transform,flight::physical(input));
        if(device==0 && frameCorrection.correctFramePose) {
            frameCorrection.Add(received,output,ipc.command,&input);
        }
    }
    hooks.originals[index](host,device,output,size);
    if(device<64) readback.CommitForward(device,generation,transform);
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
    frameCorrection.correctFramePose=readFlag("correctFramePose",false);
    frameCorrection.timeBasedFramePose=readFlag("timeBasedFramePose",false);
    char configured[sizeof(callerPolicy.hmdAnchoredSystems)]{};
    vr::EVRSettingsError settingsError=vr::VRSettingsError_None;
    vr::VRSettings()->GetString("driver_flugelkranz","hmdAnchoredTrackingSystems",configured,sizeof(configured),&settingsError);
    if(settingsError==vr::VRSettingsError_None) std::memcpy(callerPolicy.hmdAnchoredSystems,configured,sizeof(configured));
    char message[320];
    std::snprintf(message,sizeof(message),"FlugelKranz: HMD-anchored tracking systems: '%s'",callerPolicy.hmdAnchoredSystems);
    vr::VRDriverLog()->Log(message);
    char drivers[sizeof(callerPolicy.physicalPoseDrivers)]{};
    settingsError=vr::VRSettingsError_None;
    vr::VRSettings()->GetString("driver_flugelkranz","physicalPoseDrivers",drivers,sizeof(drivers),&settingsError);
    if(settingsError==vr::VRSettingsError_None) std::memcpy(callerPolicy.physicalPoseDrivers,drivers,sizeof(drivers));
    std::snprintf(message,sizeof(message),"FlugelKranz: physical raw poses for drivers: '%s'",callerPolicy.physicalPoseDrivers);
    vr::VRDriverLog()->Log(message);
    // One line with every behaviour flag, so a log always says which paths were live.
    std::snprintf(message,sizeof(message),"FlugelKranz flags: observeFrames=%d correctFramePose=%d timeBasedFramePose=%d",
        observeFrames?1:0,frameCorrection.correctFramePose?1:0,frameCorrection.timeBasedFramePose?1:0);
    vr::VRDriverLog()->Log(message);
}

class DriverProvider final : public vr::IServerTrackedDeviceProvider {

public:
    vr::EVRInitError Init(vr::IVRDriverContext* context) override {
        VR_INIT_SERVER_DRIVER_CONTEXT(context);
        if(!ipc.Open()) {Cleanup();return vr::VRInitError_Driver_Failed;}
        auto result=hooks.Initialize();
        if(result!=MH_OK) {Cleanup();return vr::VRInitError_Driver_Failed;}
        hooks.observerStopping=false;
        loadSettings();
        callerPolicy.propertiesReady=true;
        const char* versions[]={"IVRServerDriverHost_006","IVRServerDriverHost_005"};
        void* detours[]={reinterpret_cast<void*>(updated0),reinterpret_cast<void*>(updated1)};
        void* addedDetours[]={reinterpret_cast<void*>(added0),reinterpret_cast<void*>(added1)};
        int installed=0;
        for(int i=0;i<2;i++) {
            vr::EVRInitError error=vr::VRInitError_None;
            auto host=context->GetGenericInterface(versions[i],&error);
            if(!host || error!=vr::VRInitError_None) continue;
            if(observeFrames || frameCorrection.correctFramePose) {
                auto addedTarget=(*reinterpret_cast<void***>(host))[0];
                if(i==0 || addedTarget!=hooks.addedTargets[0]) {
                    if(MH_CreateHook(addedTarget,addedDetours[i],reinterpret_cast<void**>(&hooks.addedOriginal[i]))!=MH_OK) {Cleanup();return vr::VRInitError_Driver_Failed;}
                    hooks.addedTargets[i]=addedTarget;
                    if(MH_EnableHook(addedTarget)!=MH_OK) {Cleanup();return vr::VRInitError_Driver_Failed;}
                }
            }
            auto target=(*reinterpret_cast<void***>(host))[1];
            if(i==1 && target==hooks.targets[0]) continue;
            if(MH_CreateHook(target,detours[i],reinterpret_cast<void**>(&hooks.originals[i]))!=MH_OK) {Cleanup();return vr::VRInitError_Driver_Failed;}
            hooks.targets[i]=target;
            if(MH_EnableHook(target)!=MH_OK) {Cleanup();return vr::VRInitError_Driver_Failed;}
            installed++;
        }
        if(!installed) {Cleanup();return vr::VRInitError_Driver_Failed;}
        if(callerPolicy.physicalPoseDrivers[0]) {
            vr::EVRInitError hostError=vr::VRInitError_None;
            auto host=context->GetGenericInterface("IVRServerDriverHost_006",&hostError);
            auto target=host && hostError==vr::VRInitError_None?(*reinterpret_cast<void***>(host))[6]:nullptr;
            if(target && MH_CreateHook(target,reinterpret_cast<void*>(rawPoses),reinterpret_cast<void**>(&hooks.rawOriginal))==MH_OK) {
                hooks.rawTarget=target;
                vr::VRDriverLog()->Log(MH_EnableHook(target)==MH_OK
                    ?"FlugelKranz: raw pose readback hook installed"
                    :"FlugelKranz: raw pose readback hook could not be enabled");
            } else vr::VRDriverLog()->Log("FlugelKranz: raw pose readback hook failed; HMD-based solvers will see the flown HMD");
        }
        vr::VRDriverLog()->Log("FlugelKranz driver ready: raw snapshots + XYZ body-pose transforms v2");
        return vr::VRInitError_None;
    }
    void Cleanup() override {
        hooks.StopAndRemove();
        std::lock_guard<std::mutex> lock(gate);
        ipc.Close();
        hooks.Uninitialize();
        ipc.command={};ipc.lastHeartbeat=0;
        physicalHeadValid=false;
        for(auto& known:callerPolicy.anchoredDevices) known.store(0);
        readback.Reset();
        for(auto& site:callerPolicy.physicalCallSites) site.store(nullptr);
        for(auto& site:callerPolicy.flownCallSites) site.store(nullptr);
        callerPolicy.propertiesReady=false;
        frameCorrection.ClearAfterDrain();
        VR_CLEANUP_SERVER_DRIVER_CONTEXT();
    }
    const char* const* GetInterfaceVersions() override {return vr::k_InterfaceVersions;}
    void RunFrame() override {std::lock_guard<std::mutex> lock(gate);ipc.refresh();}
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
