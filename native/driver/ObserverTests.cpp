// Include the actual callbacks to test forwarding without starting SteamVR.
#include "Driver.cpp"
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
    std::puts("Observer forwards original layers and ignores unavailable/unsupported interfaces: passed");
}
