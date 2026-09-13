#include "Transform.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
void check(bool ok, const char* message) {
    if(!ok) { std::fprintf(stderr,"FAIL: %s\n",message); std::exit(1); }
}
bool near(double a,double b) { return std::abs(a-b)<1e-10; }
void vector(const double* actual,double x,double y,double z,const char* message) {
    check(near(actual[0],x)&&near(actual[1],y)&&near(actual[2],z),message);
}
int main() {
    vr::DriverPose_t p{};
    p.qRotation.w=p.qWorldFromDriverRotation.w=p.qDriverFromHeadRotation.w=1;
    p.vecPosition[0]=1;
    p.vecVelocity[0]=2; p.vecAcceleration[0]=3;
    p.vecAngularVelocity[0]=4; p.vecAngularAcceleration[0]=5;
    p.vecWorldFromDriverTranslation[1]=2;
    p.poseTimeOffset=-0.012; p.poseIsValid=p.deviceIsConnected=true;
    p.result=vr::TrackingResult_Running_OK;
    const double h=std::sqrt(0.5);
    for(int axis=0;axis<3;axis++) {
        flight::Pose t; t.w=h; t.pz=4;
        if(axis==0)t.x=h; else if(axis==1)t.y=h; else t.z=h;
        auto out=flight::apply(p,t);
        check(near(out.qWorldFromDriverRotation.w,1),"flight must be baked into body, not calibration");
        vector(out.vecWorldFromDriverTranslation,0,0,0,"world calibration translation is flattened");
        check(near(out.qRotation.w,h)&&near(out.qRotation.x,t.x)&&near(out.qRotation.y,t.y)&&near(out.qRotation.z,t.z),"body orientation rotates around each axis");
        if(axis==0) vector(out.vecPosition,1,0,6,"X rotation includes existing origin translation");
        if(axis==1) vector(out.vecPosition,0,2,3,"Y rotation includes existing origin translation");
        if(axis==2) vector(out.vecPosition,-2,1,4,"Z rotation includes existing origin translation");
        const double dx=axis==0?1:0, dy=axis==2?1:0, dz=axis==1?-1:0;
        vector(out.vecVelocity,2*dx,2*dy,2*dz,"linear velocity uses new coordinates");
        vector(out.vecAcceleration,3*dx,3*dy,3*dz,"linear acceleration uses new coordinates");
        vector(out.vecAngularVelocity,4*dx,4*dy,4*dz,"angular velocity uses new coordinates");
        vector(out.vecAngularAcceleration,5*dx,5*dy,5*dz,"angular acceleration uses new coordinates");
        check(out.poseTimeOffset==p.poseTimeOffset && out.result==p.result && out.poseIsValid && out.deviceIsConnected,"timing and tracking metadata are preserved");
    }
    // Noncommuting rotations: original world transform is Z+90, flight is X+90.
    p.qWorldFromDriverRotation={h,0,0,h};
    p.qDriverFromHeadRotation={h,0,h,0}; p.vecDriverFromHeadTranslation[0]=0.1;
    auto out=flight::apply(p,{h,0,0,h,0,0,0});
    vector(out.vecPosition,0,0,3,"world transform precedes flight transform");
    check(near(out.qRotation.w,0.5)&&near(out.qRotation.x,0.5)&&near(out.qRotation.y,-0.5)&&near(out.qRotation.z,0.5),"noncommuting orientation order");
    check(std::memcmp(&out.qDriverFromHeadRotation,&p.qDriverFromHeadRotation,sizeof(p.qDriverFromHeadRotation))==0,"head calibration orientation preserved");
    vector(out.vecDriverFromHeadTranslation,0.1,0,0,"head calibration translation preserved");
    auto head=flight::physical(out);
    check(near(head.px,0)&&near(head.py,0)&&near(head.pz,3.1),"head offset applied exactly once");
    auto reset=flight::apply(p,{});
    check(std::memcmp(&reset,&p,sizeof(p))==0,"identity reset passes original driver pose unchanged");
    auto signReset=flight::apply(p,{0,0,0,-1,0,0,0});
    check(std::memcmp(&signReset,&p,sizeof(p))==0,"negative identity quaternion also passes through");
    auto translated=flight::apply(p,{0,0,0,1,5,0,0});
    vector(translated.vecPosition,5,3,0,"translation-only follows kawaii world-space pose path");
    check(!flight::valid({0,0,0,0,0,0,0}),"invalid quaternion rejected");
    std::puts("XYZ body poses, origin composition, derivatives, calibration and reset: passed");
}
