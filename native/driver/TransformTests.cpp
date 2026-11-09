#include "FrameHistory.h"
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
    {
        // Measured on Quest 2 + VD + SlimeVR: the solver anchors trackers to the flown
        // HMD, so a tracker arrives one bone away from the flown head, not the physical one.
        flight::Pose flightTransform{0,std::sin(0.4),0,std::cos(0.4),2,0.5,-3};
        const double physicalHead[3]{0.3,1.5,-0.2};
        double flownHead[3]; flight::transformPoint(flightTransform,physicalHead,flownHead);
        const double bone[3]{0.1,-0.9,0.05};
        vr::DriverPose_t tracker{};
        tracker.qRotation.w=tracker.qWorldFromDriverRotation.w=tracker.qDriverFromHeadRotation.w=1;
        tracker.poseIsValid=tracker.deviceIsConnected=true;
        for(int i=0;i<3;i++) tracker.vecPosition[i]=flownHead[i]+bone[i];
        const vr::HmdQuaternion_t rotation{flightTransform.w,flightTransform.x,flightTransform.y,flightTransform.z};
        auto anchoredPose=flight::apply(tracker,flight::aboutPivot(flightTransform,flownHead));
        auto anchored=flight::physical(anchoredPose);
        double rotatedBone[3]; flight::rotate(rotation,bone,rotatedBone);
        check(near(anchored.px,flownHead[0]+rotatedBone[0])&&near(anchored.py,flownHead[1]+rotatedBone[1])&&near(anchored.pz,flownHead[2]+rotatedBone[2]),"anchored tracker stays one rotated bone from the flown head");
        check(near(anchoredPose.qRotation.w,rotation.w)&&near(anchoredPose.qRotation.y,rotation.y),"anchored tracker still rotates with the flight");
        // The plain transform moves the tracker a second time by where the flown head would go.
        auto full=flight::physical(flight::apply(tracker,flightTransform));
        double twiceFlown[3]; flight::transformPoint(flightTransform,flownHead,twiceFlown);
        check(near(full.px-anchored.px,twiceFlown[0]-flownHead[0])&&near(full.py-anchored.py,twiceFlown[1]-flownHead[1])&&near(full.pz-anchored.pz,twiceFlown[2]-flownHead[2]),"full transform double-applies the flight to anchored trackers");
        auto passthrough=flight::apply(tracker,flight::aboutPivot({0,0,0,1,4,0,-2},flownHead));
        check(std::memcmp(&passthrough,&tracker,sizeof(tracker))==0,"translation-only flight leaves anchored trackers untouched");
        double pivotCheck[3]; flight::transformPoint(flight::aboutPivot(flightTransform,flownHead),flownHead,pivotCheck);
        vector(pivotCheck,flownHead[0],flownHead[1],flownHead[2],"pivot stays fixed");
    }
    check(flight::listContains("slimevr","slimevr"),"exact tracking system matches");
    check(flight::listContains(" standable , SlimeVR ","slimevr"),"list match ignores case and spaces");
    check(!flight::listContains("slimevr","lighthouse"),"other tracking systems are not anchored");
    check(!flight::listContains("slimevrx","slimevr") && !flight::listContains("","slimevr"),"no partial or empty matches");
    {
        // SlimeVR reads raw poses back from inside vrserver; the readback hook must hand it
        // the body, not the flight, so its skeleton and its trackers stay physical.
        flight::Pose flightTransform{0.36,0.48,0,0.8,3,-1,2};
        vr::DriverPose_t body{};
        body.qRotation={std::cos(0.3),0,0,std::sin(0.3)};
        body.qWorldFromDriverRotation.w=body.qDriverFromHeadRotation.w=1;
        body.vecPosition[0]=0.4;body.vecPosition[1]=1.2;body.vecPosition[2]=-0.3;
        body.poseIsValid=body.deviceIsConnected=true;
        const auto physicalMatrix=flight::matrix(flight::physical(body));
        const double velocity[3]{0.5,-0.25,1.0};
        double flownVelocity[3];
        flight::rotate({flightTransform.w,flightTransform.x,flightTransform.y,flightTransform.z},velocity,flownVelocity);
        vr::TrackedDevicePose_t readback{};
        readback.mDeviceToAbsoluteTracking=flight::matrix(flight::physical(flight::apply(body,flightTransform)));
        for(int i=0;i<3;i++) readback.vVelocity.v[i]=static_cast<float>(flownVelocity[i]);
        readback.bPoseIsValid=readback.bDeviceIsConnected=true;
        const auto flown=readback;
        flight::removeTransform(readback,flightTransform);
        check(flight::matrixError(readback.mDeviceToAbsoluteTracking,physicalMatrix)<1e-6,"readback removes the flight from the pose");
        check(std::abs(readback.vVelocity.v[0]-velocity[0])<1e-5&&std::abs(readback.vVelocity.v[1]-velocity[1])<1e-5
            &&std::abs(readback.vVelocity.v[2]-velocity[2])<1e-5,"readback removes the flight from the velocity");
        auto unflown=flown;
        flight::removeTransform(unflown,flight::Pose{});
        check(std::memcmp(&unflown.mDeviceToAbsoluteTracking,&flown.mDeviceToAbsoluteTracking,sizeof(flown.mDeviceToAbsoluteTracking))==0
            &&std::memcmp(&unflown.vVelocity,&flown.vVelocity,sizeof(flown.vVelocity))==0
            &&std::memcmp(&unflown.vAngularVelocity,&flown.vAngularVelocity,sizeof(flown.vAngularVelocity))==0,
            "identity readback is left untouched");
        // A readback racing a pose update holds either the pose flown with the previous
        // transform or the new one; it must remove whichever that pose carries.
        flight::Pose previousTransform{0.36,0.48,0,0.8,2.9,-1,2};
        const auto updated=flight::physical(body);
        const double updatedPosition[3]{updated.px,updated.py,updated.pz};
        const auto oldPose=flight::matrix(flight::physical(flight::apply(body,previousTransform)));
        const auto newPose=flight::matrix(flight::physical(flight::apply(body,flightTransform)));
        auto picked=flight::readbackTransform(oldPose,previousTransform,flightTransform,updatedPosition);
        check(picked.px==previousTransform.px,"readback still holding the old pose removes the old transform");
        picked=flight::readbackTransform(newPose,previousTransform,flightTransform,updatedPosition);
        check(picked.px==flightTransform.px,"readback already holding the new pose removes the new transform");
    }
    std::puts("XYZ body poses, origin composition, derivatives, calibration, reset, HMD-anchored pivot, physical readback: passed");
}
