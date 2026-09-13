// Include the actual callbacks to test forwarding without starting SteamVR.
#include "Driver.cpp"
#include <cstdlib>
#include <limits>
namespace {
const Layer* expectedEyes=nullptr;
Layer expectedCopy[2]{};
unsigned forwarded=0;
void* expectedSelf=reinterpret_cast<void*>(0x1234);
void require(bool condition) {if(!condition) std::abort();}
void forwardedSubmit(void* self,const Layer(&eyes)[2]) {
    require(self==expectedSelf);
    require(&eyes[0]==expectedEyes);
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
    std::puts("Observer forwards original layers and ignores unavailable/unsupported interfaces: passed");
}
