#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <MinHook.h>
#include <mutex>
#include <atomic>
#include <cstring>
#include <new>
#include "Transform.h"

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
        const char* versions[]={"IVRServerDriverHost_006","IVRServerDriverHost_005"};
        void* detours[]={reinterpret_cast<void*>(updated0),reinterpret_cast<void*>(updated1)};
        int installed=0;
        for(int i=0;i<2;i++) {
            vr::EVRInitError error=vr::VRInitError_None;
            auto host=context->GetGenericInterface(versions[i],&error);
            if(!host || error!=vr::VRInitError_None) continue;
            auto target=(*reinterpret_cast<void***>(host))[1];
            if(i==1 && target==targets[0]) continue;
            if(MH_CreateHook(target,detours[i],reinterpret_cast<void**>(&originals[i]))!=MH_OK) {Cleanup();return vr::VRInitError_Driver_Failed;}
            targets[i]=target;
            if(MH_EnableHook(target)!=MH_OK) {Cleanup();return vr::VRInitError_Driver_Failed;}
            installed++;
        }
        if(!installed) {Cleanup();return vr::VRInitError_Driver_Failed;}
        vr::VRDriverLog()->Log("FlugelKranz driver ready: raw snapshots + XYZ world-from-driver transforms");
        return vr::VRInitError_None;
    }
    void Cleanup() override {
        for(auto target:targets) if(target) MH_DisableHook(target);
        // A callback may still be executing its original function/trampoline.
        // Stop new detours and let existing callbacks return before freeing it.
        while(inFlight.load()!=0) Sleep(1);
        for(auto& target:targets) if(target) {MH_RemoveHook(target);target=nullptr;}
        std::lock_guard<std::mutex> lock(gate);
        if(state) {UnmapViewOfFile(state);state=nullptr;}
        if(mapping) {CloseHandle(mapping);mapping=nullptr;}
        if(ipcMutex) {CloseHandle(ipcMutex);ipcMutex=nullptr;}
        if(minHookInitialized) {MH_Uninitialize();minHookInitialized=false;}
        command={};lastHeartbeat=0;
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
