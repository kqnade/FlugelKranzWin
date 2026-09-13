#include "FrameHistory.h"
#include <cstdio>
#include <cstdlib>
#include <limits>
void check(bool value,const char* message) {if(!value){std::fprintf(stderr,"FAIL: %s\n",message);std::exit(1);}}
vr::DriverPose_t physicalPose() {
    vr::DriverPose_t p{};
    p.qRotation.w=p.qWorldFromDriverRotation.w=p.qDriverFromHeadRotation.w=1;
    p.poseIsValid=p.deviceIsConnected=true;p.vecPosition[0]=1;p.vecPosition[1]=2;
    return p;
}
int main() {
    const double h=std::sqrt(0.5);
    auto p=physicalPose();
    for(auto transform:{flight::Pose{h,0,0,h,3,4,5},flight::Pose{0,h,0,h,3,4,5},flight::Pose{0,0,h,h,3,4,5}}) {
        auto output=flight::apply(p,transform);
        auto rendered=flight::matrix(flight::physical(output));
        auto restored=flight::removeTransform(rendered,transform);
        check(flight::matrixError(restored,flight::matrix(flight::physical(p)))<1e-8,"inverse metadata restores physical pose on every axis");
    }
    flight::FrameHistory history;
    flight::Pose old{0,h,0,h,3,0,0},latest{h,0,0,h,0,4,0};
    auto oldOutput=flight::apply(p,old);
    auto oldLayer=flight::matrix(flight::physical(oldOutput));
    history.add(1000,oldOutput,old);
    history.add(1010,flight::apply(p,latest),latest);
    auto match=history.match(1020,0.02f,oldLayer);
    check(match.found && match.transform.py==0 && match.transform.px==3,"delayed frame selects old command rather than latest");
    check(!history.match(1400,0,oldLayer).found,"expired history rejected");
    check(!history.match(1020,0.2f,oldLayer).found,"unbounded prediction rejected");
    auto invalid=oldLayer;invalid.m[0][3]=std::numeric_limits<float>::quiet_NaN();
    check(!history.match(1020,0,invalid).found,"nonfinite layer rejected");
    history.clear();
    history.add(1000,oldOutput,old);
    // Distinct commands can result in the same virtual pose after physical head
    // movement. With no frame ID it is unsafe to guess between those commands.
    history.add(1010,oldOutput,latest);
    check(!history.match(1020,0.02f,oldLayer).found,"ambiguous transforms rejected");
    history.clear();
    auto moving=p; moving.vecVelocity[2]=1;moving.vecAngularVelocity[1]=1;
    history.add(1000,moving,{});
    auto predicted=p;predicted.vecPosition[2]=0.05;
    predicted.qRotation={std::cos(0.025),0,std::sin(0.025),0};
    check(history.match(1020,0.03f,flight::matrix(flight::physical(predicted))).found,"sample age plus prediction interval accounts for physical motion");
    history.clear();
    // A held flight transform is identical for every sample in the render
    // window. Head prediction error must not switch that transform off.
    for(int i=0;i<=30;i++) history.add(2000+i*10,oldOutput,old);
    auto turned=p;turned.qRotation={h,h,0,0};
    auto turnedLayer=flight::matrix(flight::physical(flight::apply(turned,old)));
    auto held=history.match(2301,0.03f,turnedLayer);
    check(held.found && held.transform.px==3,"held transform survives physical head prediction mismatch");
    check(!history.match(2400,0.03f,turnedLayer).found,"held transform requires recent tracking");
    history.add(2310,flight::apply(p,latest),latest);
    check(!history.match(2311,0.03f,turnedLayer).found,"command change invalidates held-transform shortcut");
    history.clear();
    p.poseIsValid=false;history.add(1000,p,{});
    check(!history.match(1010,0,flight::matrix(flight::physical(p))).found,"invalid tracking not used");
    std::puts("Frame inverse, delayed commands, prediction, ambiguity and expiration: passed");
}
