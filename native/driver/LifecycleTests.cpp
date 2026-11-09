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
    ipc.ipcMutex=CreateMutexW(nullptr,FALSE,nullptr);
    ipc.mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(flight::State),nullptr);
    require(ipc.ipcMutex && ipc.mapping);
    ipc.state=static_cast<flight::State*>(MapViewOfFile(ipc.mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(flight::State)));
    require(ipc.state!=nullptr);
    new(ipc.state) flight::State{};
    ipc.command.px=3;ipc.lastHeartbeat=123;
    readback.CommitForward(0,readback.BeginForward(0,ipc.command,vr::DriverPose_t{},0),ipc.command);
    physicalHeadValid=true;callerPolicy.propertiesReady=true;
    callerPolicy.anchoredDevices[0]=2;
    callerPolicy.physicalCallSites[0]=reinterpret_cast<void*>(1);
    std::atomic<bool> finished{false};
    std::thread cleanup;
    {
        CallbackScope active{hooks};
        cleanup=std::thread([&]{provider.Cleanup();finished=true;});
        const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(2);
        while(!waitingForCallbacks && !finished && std::chrono::steady_clock::now()<deadline) {
            std::this_thread::yield();
        }
        require(waitingForCallbacks && !finished);
        // Resources are still valid while a callback holds the lifetime guard.
        require(ipc.state!=nullptr && ipc.mapping!=nullptr && ipc.ipcMutex!=nullptr);
    }
    cleanup.join();
    require(finished && hooks.inFlight==0);
    require(!ipc.state && !ipc.mapping && !ipc.ipcMutex);
    require(flight::isIdentity(ipc.command) && ipc.lastHeartbeat==0);
    require(flight::isIdentity(readback.Inspect(0).applied) && readback.Inspect(0).committed==0);
    require(!physicalHeadValid && !callerPolicy.propertiesReady && callerPolicy.anchoredDevices[0]==0);
    require(callerPolicy.physicalCallSites[0]==nullptr);
    provider.Cleanup(); // repeated/partial cleanup must remain safe
    require(!ipc.state && !ipc.mapping && !ipc.ipcMutex && hooks.inFlight==0);
    int error=-1;
    require(HmdDriverFactory(vr::IServerTrackedDeviceProvider_Version,&error)==&provider);
    require(error==vr::VRInitError_None);
    require(!HmdDriverFactory("unsupported",&error));
    require(error==vr::VRInitError_Init_InterfaceNotFound);
    std::puts("cleanup: waits for callbacks, releases anonymous IPC, clears state, repeat cleanup: passed");
}
