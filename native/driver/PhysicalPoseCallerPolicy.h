#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <atomic>
#include <array>
#include <mutex>
#include <cstdio>
#include "Transform.h"

namespace flight {
// Configuration is loaded before hooks are enabled; caches are cleared after callback drain.
struct PhysicalPoseCallerPolicy {
// SlimeVR-style solvers build their skeleton around the HMD pose they read back
// from SteamVR, so their tracker poses already contain the flight translation.
// 0 = not classified yet, 1 = own tracking, 2 = anchored to the flown HMD.
std::array<std::atomic<uint8_t>,64> anchoredDevices{};
char hmdAnchoredSystems[256]="";
std::atomic<bool> propertiesReady{false};
char physicalPoseDrivers[256]="driver_slimevr.dll,driver_rebocap.dll";
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

};
}
