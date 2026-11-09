#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <MinHook.h>
#include <vector>
#include <utility>
#include <cstdlib>
#include <atomic>
#include <thread>
#include <chrono>
static std::vector<std::pair<char,uintptr_t>> calls;
static bool failInit=false;
static std::atomic<bool> waiting{false};
static void WINAPI testSleep(DWORD milliseconds) {waiting=true;::Sleep(milliseconds);}
static MH_STATUS WINAPI testInitialize() {return failInit?MH_ERROR_MEMORY_ALLOC:MH_OK;}
static MH_STATUS WINAPI testDisable(void* target) {calls.emplace_back('d',reinterpret_cast<uintptr_t>(target));return MH_OK;}
static MH_STATUS WINAPI testRemove(void* target) {calls.emplace_back('r',reinterpret_cast<uintptr_t>(target));return MH_OK;}
static MH_STATUS WINAPI testUninitialize() {calls.emplace_back('u',0);return MH_OK;}
#define MH_Initialize testInitialize
#define MH_DisableHook testDisable
#define MH_RemoveHook testRemove
#define MH_Uninitialize testUninitialize
#define Sleep testSleep
#include "HookRegistration.h"
#undef Sleep
#undef MH_Initialize
#undef MH_DisableHook
#undef MH_RemoveHook
#undef MH_Uninitialize

int main() {
    auto require=[](bool value){if(!value) std::abort();};
    flight::HookRegistration hooks;
    require(hooks.Initialize()==MH_OK && hooks.minHookInitialized);
    hooks.addedTargets[0]=reinterpret_cast<void*>(1);
    hooks.addedTargets[1]=reinterpret_cast<void*>(2);
    hooks.componentTarget=reinterpret_cast<void*>(3);
    hooks.submitTarget=reinterpret_cast<void*>(4);
    hooks.targets[0]=reinterpret_cast<void*>(5);
    hooks.targets[1]=reinterpret_cast<void*>(6);
    hooks.rawTarget=reinterpret_cast<void*>(7);
    std::atomic<bool> finished{false};
    std::thread cleanup;
    {
        flight::HookRegistration::CallbackScope active{hooks};
        cleanup=std::thread([&]{hooks.StopAndRemove();finished=true;});
        const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(2);
        while(!waiting && !finished && std::chrono::steady_clock::now()<deadline)
            std::this_thread::yield();
        require(waiting && !finished);
        // The wait publishes all preceding disable calls; removal must not have begun.
        require(calls.size()==7);
        for(uintptr_t i=1;i<=7;i++) require(calls[i-1]==std::make_pair('d',i));
    }
    cleanup.join();
    require(hooks.observerStopping && hooks.inFlight==0 && calls.size()==14);
    for(uintptr_t i=1;i<=7;i++) {
        require(calls[i-1]==std::make_pair('d',i));
        require(calls[i+6]==std::make_pair('r',i));
    }
    hooks.Uninitialize();
    require(!hooks.minHookInitialized && calls.back().first=='u');
    const auto count=calls.size();
    hooks.StopAndRemove();hooks.Uninitialize();
    require(calls.size()==count);
    failInit=true;
    require(hooks.Initialize()==MH_ERROR_MEMORY_ALLOC && !hooks.minHookInitialized);
    hooks.StopAndRemove();hooks.Uninitialize();
    require(calls.size()==count);
}
