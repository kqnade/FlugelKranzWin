#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <MinHook.h>
#include <openvr_driver.h>
#include <mutex>
#include <atomic>

namespace flight {
// Owns callback pointers and hook lifetime. Dynamic installation is serialized by
// observerGate. Provider keeps IPC and history alive until StopAndRemove returns.
struct HookRegistration {
    using Callback=void(*)(void*,uint32_t,const vr::DriverPose_t&,uint32_t);
    using Added=bool(*)(void*,const char*,vr::ETrackedDeviceClass,vr::ITrackedDeviceServerDriver*);
    using Component=void*(*)(void*,const char*);
    using Layer=vr::IVRDriverDirectModeComponent::SubmitLayerPerEye_t;
    using Submit=void(*)(void*,const Layer(&)[2]);
    using RawPoses=void(*)(void*,float,vr::TrackedDevicePose_t*,uint32_t);
    Callback originals[2]{};
    void* targets[2]{};
    Added addedOriginal[2]{};
    void* addedTargets[2]{};
    Component componentOriginal=nullptr;
    Submit submitOriginal=nullptr;
    void* componentTarget=nullptr;
    void* submitTarget=nullptr;
    RawPoses rawOriginal=nullptr;
    void* rawTarget=nullptr;
    std::mutex observerGate;
    bool observerStopping=false; // protected by observerGate
    std::atomic<unsigned> inFlight{0};
    bool minHookInitialized=false;
    struct CallbackScope {
        HookRegistration& owner;
        explicit CallbackScope(HookRegistration& owner):owner(owner) {owner.inFlight.fetch_add(1);}
        ~CallbackScope() {owner.inFlight.fetch_sub(1);}
        CallbackScope(const CallbackScope&)=delete;
        CallbackScope& operator=(const CallbackScope&)=delete;
    };
    MH_STATUS Initialize() {
        const auto result=MH_Initialize();
        if(result==MH_OK) minHookInitialized=true;
        return result;
    }
    void StopAndRemove() {
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
    }
    void Uninitialize() {
        if(minHookInitialized) {MH_Uninitialize();minHookInitialized=false;}
    }
};
}
