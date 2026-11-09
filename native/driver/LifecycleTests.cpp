// Exercise cleanup without installing hooks or touching SteamVR's named IPC objects.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <atomic>
static std::atomic<bool> waitingForCallbacks{false};
static void WINAPI trackedSleep(DWORD milliseconds) {
    waitingForCallbacks=true;
    ::Sleep(milliseconds);
}
#define Sleep trackedSleep
#include "Driver.cpp"
#undef Sleep
#include "DriverHarness.h"
#include <cstdlib>

namespace {
void require(bool value) {if(!value) std::abort();}
}
int main() {
    harness::install();
    ipcMutex=CreateMutexW(nullptr,FALSE,nullptr);
    mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(flight::State),nullptr);
    require(ipcMutex && mapping);
    state=static_cast<flight::State*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(flight::State)));
    require(state!=nullptr);
    new(state) flight::State{};
    command.px=3;lastHeartbeat=123;
    readback.CommitForward(0,readback.BeginForward(0,command,flight::Pose{}),command);
    physicalHeadValid=true;propertiesReady=true;
    anchoredDevices[0]=2;
    physicalCallSites[0]=reinterpret_cast<void*>(1);
    std::atomic<bool> finished{false};
    std::thread cleanup;
    {
        CallbackScope active;
        cleanup=std::thread([&]{provider.Cleanup();finished=true;});
        const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(2);
        while(!waitingForCallbacks && !finished && std::chrono::steady_clock::now()<deadline) {
            std::this_thread::yield();
        }
        require(waitingForCallbacks && !finished);
        // Resources are still valid while a callback holds the lifetime guard.
        require(state!=nullptr && mapping!=nullptr && ipcMutex!=nullptr);
    }
    cleanup.join();
    require(finished && inFlight==0);
    require(!state && !mapping && !ipcMutex);
    require(flight::isIdentity(command) && lastHeartbeat==0);
    require(flight::isIdentity(readback.Inspect(0).applied) && readback.Inspect(0).committed==0);
    require(!physicalHeadValid && !propertiesReady && anchoredDevices[0]==0);
    require(physicalCallSites[0]==nullptr);
    provider.Cleanup(); // repeated/partial cleanup must remain safe
    require(!state && !mapping && !ipcMutex && inFlight==0);
    int error=-1;
    require(HmdDriverFactory(vr::IServerTrackedDeviceProvider_Version,&error)==&provider);
    require(error==vr::VRInitError_None);
    require(!HmdDriverFactory("unsupported",&error));
    require(error==vr::VRInitError_Init_InterfaceNotFound);
    std::puts("cleanup: waits for callbacks, releases anonymous IPC, clears state, repeat cleanup: passed");
}
