#pragma once
#include <cmath>
#include <cstdint>
#include <openvr_driver.h>

namespace flight {
struct Pose { double x=0,y=0,z=0,w=1,px=0,py=0,pz=0; };
inline vr::HmdQuaternion_t multiply(vr::HmdQuaternion_t a, vr::HmdQuaternion_t b) {
    return {a.w*b.w-a.x*b.x-a.y*b.y-a.z*b.z,
        a.w*b.x+a.x*b.w+a.y*b.z-a.z*b.y,
        a.w*b.y-a.x*b.z+a.y*b.w+a.z*b.x,
        a.w*b.z+a.x*b.y-a.y*b.x+a.z*b.w};
}
inline void rotate(vr::HmdQuaternion_t q, const double* p, double* out) {
    auto r=multiply(multiply(q,{0,p[0],p[1],p[2]}),{q.w,-q.x,-q.y,-q.z});
    out[0]=r.x; out[1]=r.y; out[2]=r.z;
}
inline bool valid(Pose p) {
    return std::isfinite(p.px)&&std::isfinite(p.py)&&std::isfinite(p.pz)
        &&std::isfinite(p.x)&&std::isfinite(p.y)&&std::isfinite(p.z)&&std::isfinite(p.w)
        &&std::abs(p.x*p.x+p.y*p.y+p.z*p.z+p.w*p.w-1)<0.0001;
}
inline vr::DriverPose_t apply(vr::DriverPose_t p, Pose transform) {
    vr::HmdQuaternion_t q{transform.w,transform.x,transform.y,transform.z};
    double t[3]; rotate(q,p.vecWorldFromDriverTranslation,t);
    p.qWorldFromDriverRotation=multiply(q,p.qWorldFromDriverRotation);
    p.vecWorldFromDriverTranslation[0]=t[0]+transform.px;
    p.vecWorldFromDriverTranslation[1]=t[1]+transform.py;
    p.vecWorldFromDriverTranslation[2]=t[2]+transform.pz;
    // Body pose, head/IMU calibration and velocities stay in driver coordinates.
    // The runtime applies the changed world-from-driver transform to all of them.
    return p;
}
inline Pose physical(const vr::DriverPose_t& p) {
    auto body=multiply(p.qWorldFromDriverRotation,p.qRotation);
    auto q=multiply(body,p.qDriverFromHeadRotation);
    double position[3], head[3];
    rotate(p.qWorldFromDriverRotation,p.vecPosition,position);
    rotate(body,p.vecDriverFromHeadTranslation,head);
    return {q.x,q.y,q.z,q.w,
        position[0]+p.vecWorldFromDriverTranslation[0]+head[0],
        position[1]+p.vecWorldFromDriverTranslation[1]+head[1],
        position[2]+p.vecWorldFromDriverTranslation[2]+head[2]};
}
struct Sample { uint64_t timestamp=0; uint32_t flags=0,reserved=0; Pose pose; };
struct State {
    uint32_t magic=0x464b4452,version=1;
    uint64_t driverHeartbeat=0,clientHeartbeat=0;
    uint32_t owner=0,enabled=0;
    Pose command;
    uint32_t count=64,reserved=0;
    Sample samples[64];
};
static_assert(sizeof(Pose)==56 && sizeof(Sample)==72 && sizeof(State)==4704);
}
