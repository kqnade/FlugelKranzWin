#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <new>
#include "Transform.h"

namespace flight {
// The provider's gate serializes command access and lifetime. The Windows mutex only
// protects shared bytes; hot pose paths never wait on it. Close follows callback drain.
struct DriverIpc {
    HANDLE mapping=nullptr,ipcMutex=nullptr;
    State* state=nullptr;
    Pose command;
    uint64_t lastHeartbeat=0;
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
    bool Open() {
        ipcMutex=CreateMutexW(nullptr,FALSE,L"Local\\FlugelKranz.Driver.Mutex.v1");
        mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(State),L"Local\\FlugelKranz.Driver.State.v1");
        if(!mapping || !ipcMutex) return false;
        state=static_cast<State*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(State)));
        if(!state || !enter()) return false;
        new(state) State{};
        state->driverHeartbeat=GetTickCount64();
        ReleaseMutex(ipcMutex);
        return true;
    }
    void Close() {
        if(state) {UnmapViewOfFile(state);state=nullptr;}
        if(mapping) {CloseHandle(mapping);mapping=nullptr;}
        if(ipcMutex) {CloseHandle(ipcMutex);ipcMutex=nullptr;}
    }
    void PublishSample(uint32_t device,const vr::DriverPose_t& input) {
        if(state && device<64 && enter(0)) {
            auto& sample=state->samples[device];
            sample.pose=physical(input);
            sample.flags=(input.deviceIsConnected?1u:0u)|(input.poseIsValid?2u:0u);
            sample.timestamp=GetTickCount64();
            ReleaseMutex(ipcMutex);
        }
    }
};
}
