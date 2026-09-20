#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
// Model a heartbeat written while refresh is waiting for the IPC mutex.
static bool scriptedClock=false;
static unsigned clockReads=0;
static ULONGLONG WINAPI testTickCount64() {
    if (!scriptedClock) return ::GetTickCount64();
    return clockReads++ == 0 ? 1000 : 1016;
}
#define GetTickCount64 testTickCount64
// Include the actual callbacks to test forwarding without starting SteamVR.
#include "Driver.cpp"
#undef GetTickCount64
#include <cstdlib>
#include <limits>
namespace {
const Layer* expectedEyes=nullptr;
Layer expectedCopy[2]{};
unsigned forwarded=0;
bool expectCopy=false;
void* expectedSelf=reinterpret_cast<void*>(0x1234);
void require(bool condition) {if(!condition) std::abort();}
void forwardedSubmit(void* self,const Layer(&eyes)[2]) {
    require(self==expectedSelf);
    require((&eyes[0]!=expectedEyes)==expectCopy);
    require(std::memcmp(eyes,expectedCopy,sizeof(expectedCopy))==0);
    forwarded++;
}
void* componentResult=nullptr;
void* forwardedComponent(void* self,const char*) {
    require(self==expectedSelf);
    forwarded++;
    return componentResult;
}
}
int main() {
    flight::State shared{};
    state=&shared;
    ipcMutex=CreateMutexW(nullptr,FALSE,nullptr);
    require(ipcMutex!=nullptr);
    shared.owner=123;
    shared.enabled=1;
    shared.clientHeartbeat=1016;
    shared.command.px=3;
    scriptedClock=true;
    refresh();
    // A fresh heartbeat newer than the pre-lock clock must not revoke the
    // owner or drop a held Drag offset to the identity transform.
    require(shared.owner==123 && shared.enabled==1);
    require(command.px==3 && lastHeartbeat==1016);
    require(shared.driverHeartbeat==1016);
    // The stale-client fail-safe remains active at the original boundary.
    shared.clientHeartbeat=517; // 499 ms old at the locked clock sample
    refresh();
    require(shared.owner==123 && command.px==3);
    shared.clientHeartbeat=516; // 500 ms old
    refresh();
    require(shared.owner==0 && shared.enabled==0 && command.px==0);
    shared.owner=123;shared.enabled=1;shared.clientHeartbeat=1017;
    refresh(); // a genuinely future timestamp is still rejected
    require(shared.owner==0 && shared.enabled==0 && command.px==0);
    scriptedClock=false;
    CloseHandle(ipcMutex);ipcMutex=nullptr;state=nullptr;
    command={};lastHeartbeat=0;
    Layer eyes[2]{};
    eyes[0].hTexture=12;eyes[1].hTexture=34;
    eyes[0].mHmdPose.m[0][0]=1;
    eyes[1].mHmdPose.m[1][2]=0.6f;
    eyes[0].flHmdPosePredictionTimeInSecondsFromNow=0.013f;
    std::memcpy(expectedCopy,eyes,sizeof(eyes));
    expectedEyes=eyes;
    submitOriginal=forwardedSubmit;
    // Logging disabled here: verifies the production forwarding path in isolation.
    nextFrameLog=std::numeric_limits<uint64_t>::max();
    observedSubmit(expectedSelf,eyes);
    require(forwarded==1 && inFlight==0);
    require(std::memcmp(eyes,expectedCopy,sizeof(eyes))==0);
    componentOriginal=forwardedComponent;
    require(observedComponent(expectedSelf,vr::IVRDriverDirectModeComponent_Version)==nullptr);
    componentResult=reinterpret_cast<void*>(0x5678);
    require(observedComponent(expectedSelf,"IVRDriverDirectModeComponent_008")==componentResult);
    require(submitTarget==nullptr && componentTarget==nullptr);
    require(forwarded==3 && inFlight==0);
    vr::DriverPose_t head{};
    head.qRotation.w=head.qWorldFromDriverRotation.w=head.qDriverFromHeadRotation.w=1;
    head.poseIsValid=head.deviceIsConnected=true;
    head.vecPosition[0]=1;
    flight::Pose transform{0,std::sqrt(0.5),0,std::sqrt(0.5),3,0,0};
    auto output=flight::apply(head,transform);
    for(auto& eye:eyes) {
        eye.mHmdPose=flight::matrix(flight::physical(output));
        eye.flHmdPosePredictionTimeInSecondsFromNow=0.02f;
        eye.hDepthTexture=56;eye.bounds.uMin=0.2f;eye.mProjection.m[2][2]=0.75f;
    }
    Layer originalCopy[2];std::memcpy(originalCopy,eyes,sizeof(eyes));
    std::memcpy(expectedCopy,eyes,sizeof(eyes));
    for(auto& eye:expectedCopy) eye.mHmdPose=flight::matrix(flight::physical(head));
    // Float rounding after inverse multiplication is checked separately; retain
    // the full byte-level check for all fields outside the pose matrix.
    for(int i=0;i<2;i++) expectedCopy[i].mHmdPose=flight::removeTransform(eyes[i].mHmdPose,transform);
    correctFramePose=true;expectCopy=true;
    frameHistory.add(frameTimeMs(),output,transform);
    observedSubmit(expectedSelf,eyes);
    require(forwarded==4 && inFlight==0);
    require(std::memcmp(eyes,originalCopy,sizeof(eyes))==0);
    frameHistory.clear();expectCopy=false;
    std::memcpy(expectedCopy,eyes,sizeof(eyes));
    observedSubmit(expectedSelf,eyes);
    require(forwarded==5 && inFlight==0);
    timeBasedFramePose=true;expectCopy=true;
    frameHistory.add(frameTimeMs(),output,transform,&head);
    for(auto& eye:expectedCopy) eye.mHmdPose=flight::matrix(flight::physical(head));
    observedSubmit(expectedSelf,eyes);
    require(forwarded==6 && inFlight==0);
    require(std::memcmp(eyes,originalCopy,sizeof(eyes))==0);
    // Identity must preserve even a render pose that differs from our own
    // prediction: forward the exact original pointer and bytes.
    frameHistory.clear();
    const double resetNow=frameTimeMs();
    for(int dt=300;dt>=0;dt-=10) frameHistory.add(resetNow-dt,head,flight::Pose{},&head);
    expectCopy=false;
    std::memcpy(expectedCopy,eyes,sizeof(eyes));
    auto missedBefore=missedLayers.load();
    observedSubmit(expectedSelf,eyes);
    require(forwarded==7 && inFlight==0);
    require(std::memcmp(eyes,originalCopy,sizeof(eyes))==0);
    require(missedLayers.load()==missedBefore);
    // Stable flight keeps the HMD driver's render prediction, even when it
    // disagrees with the independent physical prediction in our history.
    frameHistory.clear();
    head.vecVelocity[0]=5;
    const double heldNow=frameTimeMs();
    for(int dt=300;dt>=0;dt-=10) frameHistory.add(heldNow-dt,output,transform,&head);
    for(int i=0;i<2;i++) expectedCopy[i].mHmdPose=flight::removeTransform(eyes[i].mHmdPose,transform);
    expectCopy=true;
    auto heldBefore=heldLayers.load();
    observedSubmit(expectedSelf,eyes);
    require(forwarded==8 && inFlight==0);
    require(heldLayers.load()==heldBefore+1);
    require(std::memcmp(eyes,originalCopy,sizeof(eyes))==0);
    std::puts("Observer forwards original layers and ignores unavailable/unsupported interfaces: passed");
}
