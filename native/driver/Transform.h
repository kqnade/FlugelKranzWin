#pragma once
#include <cctype>
#include <cmath>
#include <cstdint>
#include <cstring>
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
// Exact identity (either quaternion sign), the value the app sends for reset/OFF.
inline bool isIdentity(Pose p) {
    return p.x==0 && p.y==0 && p.z==0 && std::abs(p.w)==1
        && p.px==0 && p.py==0 && p.pz==0;
}
inline vr::DriverPose_t apply(vr::DriverPose_t p, Pose transform) {
    // Reset / disconnected client: preserve the original driver's representation.
    if(isIdentity(transform)) return p;
    vr::HmdQuaternion_t q{transform.w,transform.x,transform.y,transform.z};
    auto bodyToWorld=multiply(q,p.qWorldFromDriverRotation);
    double t[3]; rotate(q,p.vecWorldFromDriverTranslation,t);
    // Follow Kawaii Move Assist's world-space body-pose representation. Putting
    // flight into world-from-driver instead changes calibration metadata as well.
    rotate(bodyToWorld,p.vecPosition,p.vecPosition);
    p.vecPosition[0]+=t[0]+transform.px;
    p.vecPosition[1]+=t[1]+transform.py;
    p.vecPosition[2]+=t[2]+transform.pz;
    p.qRotation=multiply(bodyToWorld,p.qRotation);
    rotate(bodyToWorld,p.vecVelocity,p.vecVelocity);
    rotate(bodyToWorld,p.vecAcceleration,p.vecAcceleration);
    rotate(bodyToWorld,p.vecAngularVelocity,p.vecAngularVelocity);
    rotate(bodyToWorld,p.vecAngularAcceleration,p.vecAngularAcceleration);
    p.qWorldFromDriverRotation={1,0,0,0};
    for(auto& value:p.vecWorldFromDriverTranslation) value=0;
    // Head/IMU calibration stays body-local. Pose prediction timing is unchanged.
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
inline void transformPoint(Pose transform, const double* point, double* out) {
    rotate({transform.w,transform.x,transform.y,transform.z},point,out);
    out[0]+=transform.px; out[1]+=transform.py; out[2]+=transform.pz;
}
// The same rotation as `transform`, pivoted so that `pivot` stays where it is.
inline Pose aboutPivot(Pose transform, const double* pivot) {
    double rotated[3]; rotate({transform.w,transform.x,transform.y,transform.z},pivot,rotated);
    transform.px=pivot[0]-rotated[0];
    transform.py=pivot[1]-rotated[1];
    transform.pz=pivot[2]-rotated[2];
    return transform;
}
// Case-insensitive, whole-name match against a comma-separated list.
inline bool listContains(const char* list, const char* name) {
    if(!list || !name || !*name) return false;
    const size_t length=std::strlen(name);
    for(const char* cursor=list;*cursor;) {
        while(*cursor==' ' || *cursor==',') cursor++;
        const char* start=cursor;
        while(*cursor && *cursor!=',') cursor++;
        const char* end=cursor;
        while(end>start && end[-1]==' ') end--;
        if(static_cast<size_t>(end-start)!=length) continue;
        size_t i=0;
        while(i<length && std::tolower(static_cast<unsigned char>(start[i]))==std::tolower(static_cast<unsigned char>(name[i]))) i++;
        if(i==length) return true;
    }
    return false;
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
