#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdlib>
#include <cwchar>
static bool failMutex=false,failMapping=false;
static unsigned closedHandles=0;
static void require(bool value) {if(!value) std::abort();}
static HANDLE WINAPI testCreateMutex(LPSECURITY_ATTRIBUTES attributes,BOOL owned,LPCWSTR name) {
    require(std::wcscmp(name,L"Local\\FlugelKranz.Driver.Mutex.v1")==0);
    return failMutex ? nullptr : ::CreateMutexW(attributes,owned,nullptr);
}
static HANDLE WINAPI testCreateMapping(HANDLE file,LPSECURITY_ATTRIBUTES attributes,DWORD protection,DWORD high,DWORD low,LPCWSTR name) {
    require(std::wcscmp(name,L"Local\\FlugelKranz.Driver.State.v1")==0);
    return failMapping ? nullptr : ::CreateFileMappingW(file,attributes,protection,high,low,nullptr);
}
// Assert production IPC names while redirecting creation to anonymous test objects.
static BOOL WINAPI testCloseHandle(HANDLE handle) {
    const BOOL closed=::CloseHandle(handle);
    require(closed!=FALSE);
    closedHandles++;
    return closed;
}
#define CreateMutexW testCreateMutex
#define CreateFileMappingW testCreateMapping
#define CloseHandle testCloseHandle
#include "DriverIpc.h"
#undef CreateMutexW
#undef CreateFileMappingW
#undef CloseHandle
static void closeAndCheck(flight::DriverIpc& ipc) {
    const unsigned expected=closedHandles+(ipc.ipcMutex?1:0)+(ipc.mapping?1:0);
    ipc.Close();
    require(closedHandles==expected);
    require(!ipc.state && !ipc.mapping && !ipc.ipcMutex);
}
int main() {
    flight::DriverIpc ipc;
    require(ipc.Open());
    require(ipc.state && ipc.state->driverHeartbeat!=0 && ipc.state->owner==0);
    closeAndCheck(ipc);ipc.Close();
    require(!ipc.state && !ipc.mapping && !ipc.ipcMutex);
    failMutex=true;
    require(!ipc.Open());
    closeAndCheck(ipc);
    require(!ipc.state && !ipc.mapping && !ipc.ipcMutex);
    failMutex=false;failMapping=true;
    require(!ipc.Open());
    closeAndCheck(ipc);
    require(!ipc.state && !ipc.mapping && !ipc.ipcMutex);
}
