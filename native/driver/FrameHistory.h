#pragma once
#include "Transform.h"
#include <array>
#include <algorithm>

namespace flight {
inline vr::HmdMatrix34_t matrix(Pose p) {
    vr::HmdMatrix34_t m{};
    const vr::HmdQuaternion_t q{p.w,p.x,p.y,p.z};
    for(int column=0;column<3;column++) {
        double axis[3]{}, rotated[3]; axis[column]=1;rotate(q,axis,rotated);
        for(int row=0;row<3;row++) m.m[row][column]=static_cast<float>(rotated[row]);
    }
    m.m[0][3]=static_cast<float>(p.px);m.m[1][3]=static_cast<float>(p.py);m.m[2][3]=static_cast<float>(p.pz);
    return m;
}
inline double matrixError(const vr::HmdMatrix34_t& a,const vr::HmdMatrix34_t& b) {
    double rotation=0,position=0;
    for(int row=0;row<3;row++) {
        for(int col=0;col<3;col++) {double d=a.m[row][col]-b.m[row][col];rotation+=d*d;}
        double d=a.m[row][3]-b.m[row][3];position+=d*d;
    }
    // Bound both orientation (~0.57 degrees) and translation (5mm).
    return std::max(rotation/0.0002,position/0.000025);
}
inline vr::HmdMatrix34_t removeTransform(const vr::HmdMatrix34_t& rendered,Pose transform) {
    vr::HmdMatrix34_t result{};
    vr::HmdQuaternion_t inverse{transform.w,-transform.x,-transform.y,-transform.z};
    for(int column=0;column<4;column++) {
        double v[3]={rendered.m[0][column],rendered.m[1][column],rendered.m[2][column]},out[3];
        if(column==3) {v[0]-=transform.px;v[1]-=transform.py;v[2]-=transform.pz;}
        rotate(inverse,v,out);
        for(int row=0;row<3;row++) result.m[row][column]=static_cast<float>(out[row]);
    }
    return result;
}
struct FrameMatch {bool found=false;Pose transform;double error=1;bool held=false;};
struct PhysicalFrame {bool found=false;Pose pose;double predictionSeconds=0;bool interpolated=false;};
class FrameHistory {
    struct Entry {double tick=0;vr::DriverPose_t output{};Pose transform;vr::DriverPose_t original{};bool hasOriginal=false;};
    std::array<Entry,128> entries{};
    size_t count=0,next=0;
    double heldSince=0,lastTick=0;
    Pose heldTransform;
    static bool same(Pose a,Pose b) {
        return a.x==b.x && a.y==b.y && a.z==b.z && a.w==b.w
            && a.px==b.px && a.py==b.py && a.pz==b.pz;
    }
public:
    void clear() {count=next=0;heldSince=lastTick=0;}
    void add(double tick,const vr::DriverPose_t& output,Pose transform,const vr::DriverPose_t* original=nullptr) {
        if(!output.poseIsValid || !output.deviceIsConnected || !valid(transform)
            || !valid(physical(output)) || !std::isfinite(tick)) {clear();return;}
        if(!count || tick<lastTick || tick-lastTick>100 || !same(transform,heldTransform)) {
            heldSince=tick;heldTransform=transform;
        }
        lastTick=tick;
        entries[next]={tick,output,transform,original?*original:vr::DriverPose_t{},original!=nullptr};next=(next+1)%entries.size();count=std::min(count+1,entries.size());
    }
    bool canBypassCorrection(double now) const {
        // Keep correcting late virtual frames until the supported history window
        // contains only identity output. Then preserve the HMD driver's prediction.
        return count && std::isfinite(now) && now>=lastTick && now-lastTick<=50
            && lastTick-heldSince>=250
            && heldTransform.x==0 && heldTransform.y==0 && heldTransform.z==0
            && std::abs(heldTransform.w)==1
            && heldTransform.px==0 && heldTransform.py==0 && heldTransform.pz==0;
    }
    PhysicalFrame physicalAt(double now,float prediction) const {
        // SubmitLayer specifies when this frame's HMD pose was predicted to.
        // Use that time to recover the physical pose, independently of flight.
        if(!count || !std::isfinite(prediction) || std::abs(prediction)>0.1f
            || now<lastTick || now-lastTick>50) return {};
        const double target=now+prediction*1000;
        const Entry* before=nullptr;const Entry* after=nullptr;
        double beforeTime=0,afterTime=0;
        for(size_t i=0;i<count;i++) {
            const auto& e=entries[i];
            if(!e.hasOriginal || now<e.tick || now-e.tick>250
                || !e.original.poseIsValid || !e.original.deviceIsConnected
                || !std::isfinite(e.original.poseTimeOffset) || !valid(physical(e.original))) continue;
            const double time=e.tick+e.original.poseTimeOffset*1000;
            if(time<=target && (!before || time>beforeTime)) {before=&e;beforeTime=time;}
            if(time>=target && (!after || time<afterTime)) {after=&e;afterTime=time;}
        }
        if(before && after && afterTime>beforeTime && afterTime-beforeTime<=100) {
            auto a=physical(before->original),b=physical(after->original);
            const double t=(target-beforeTime)/(afterTime-beforeTime);
            double dot=a.x*b.x+a.y*b.y+a.z*b.z+a.w*b.w;
            if(dot<0) {b.x=-b.x;b.y=-b.y;b.z=-b.z;b.w=-b.w;dot=-dot;}
            double wa=1-t,wb=t;
            if(dot<0.9995) {
                const double theta=std::acos(std::clamp(dot,0.0,1.0)),s=std::sin(theta);
                wa=std::sin((1-t)*theta)/s;wb=std::sin(t*theta)/s;
            }
            Pose p{wa*a.x+wb*b.x,wa*a.y+wb*b.y,wa*a.z+wb*b.z,wa*a.w+wb*b.w,
                a.px+(b.px-a.px)*t,a.py+(b.py-a.py)*t,a.pz+(b.pz-a.pz)*t};
            double n=std::sqrt(p.x*p.x+p.y*p.y+p.z*p.z+p.w*p.w);
            p.x/=n;p.y/=n;p.z/=n;p.w/=n;
            return {valid(p),p,0,true};
        }
        const Entry* entry=before?before:after;
        if(!entry) return {};
        const double dt=(target-(before?beforeTime:afterTime))/1000;
        if(std::abs(dt)>0.1) return {};
        auto p=entry->original;
        for(int j=0;j<3;j++) p.vecPosition[j]+=p.vecVelocity[j]*dt;
        double speed=std::sqrt(p.vecAngularVelocity[0]*p.vecAngularVelocity[0]+p.vecAngularVelocity[1]*p.vecAngularVelocity[1]+p.vecAngularVelocity[2]*p.vecAngularVelocity[2]);
        if(!std::isfinite(speed)) return {};
        if(speed>0) {
            const double s=std::sin(speed*dt/2)/speed;
            p.qRotation=multiply({std::cos(speed*dt/2),s*p.vecAngularVelocity[0],s*p.vecAngularVelocity[1],s*p.vecAngularVelocity[2]},p.qRotation);
        }
        auto pose=physical(p);
        return {valid(pose),pose,dt,false};
    }
    FrameMatch heldMatch(double now,float prediction,const vr::HmdMatrix34_t& layer) const {
        if(!count || !std::isfinite(now) || now<lastTick || now-lastTick>50
            || lastTick-heldSince<250 || !std::isfinite(prediction) || std::abs(prediction)>0.1f) return {};
        for(const auto& row:layer.m) for(float value:row) if(!std::isfinite(value)) return {};
        for(int i=0;i<3;i++) for(int j=0;j<3;j++) {
            double dot=0;for(int k=0;k<3;k++) dot+=layer.m[i][k]*layer.m[j][k];
            if(std::abs(dot-(i==j?1:0))>0.001) return {};
        }
        return {true,heldTransform,0,true};
    }
    FrameMatch match(double now,float prediction,const vr::HmdMatrix34_t& layer) const {
        FrameMatch best;
        if(!std::isfinite(prediction) || prediction<0 || prediction>0.1f) return best;
        for(const auto& row:layer.m) for(float value:row) if(!std::isfinite(value)) return best;
        for(int i=0;i<3;i++) for(int j=0;j<3;j++) {
            double dot=0;for(int k=0;k<3;k++) dot+=layer.m[i][k]*layer.m[j][k];
            if(std::abs(dot-(i==j?1:0))>0.001) return best;
        }
        // When all output poses across the supported 250ms render window use
        // exactly the same command, pose prediction is unnecessary for selecting
        // that command. Preserve correction during rapid physical head motion.
        // Require continued tracking; a command change/gap restarts the window.
        if(count && lastTick-heldSince>=250 && now>=lastTick && now-lastTick<=50)
            return {true,heldTransform,0,true};
        // SubmitLayer does not carry our command ID. Match against predicted
        // output poses, and reject plausible matches with different transforms.
        std::array<FrameMatch,128> candidates{};size_t candidateCount=0;
        for(size_t i=0;i<count;i++) {
            const auto& entry=entries[i];
            if(now<entry.tick || now-entry.tick>250) continue;
            auto p=entry.output;
            double dt=static_cast<double>(now-entry.tick)/1000+prediction-p.poseTimeOffset;
            if(!std::isfinite(dt) || dt<0 || dt>0.1) continue;
            for(int j=0;j<3;j++) p.vecPosition[j]+=p.vecVelocity[j]*dt;
            double speed=std::sqrt(p.vecAngularVelocity[0]*p.vecAngularVelocity[0]+p.vecAngularVelocity[1]*p.vecAngularVelocity[1]+p.vecAngularVelocity[2]*p.vecAngularVelocity[2]);
            if(speed>0) {
                double s=std::sin(speed*dt/2)/speed;
                p.qRotation=multiply({std::cos(speed*dt/2),s*p.vecAngularVelocity[0],s*p.vecAngularVelocity[1],s*p.vecAngularVelocity[2]},p.qRotation);
            }
            auto predicted=physical(p);
            if(!valid(predicted)) continue;
            double error=matrixError(layer,matrix(predicted));
            if(!std::isfinite(error) || error>1) continue;
            candidates[candidateCount++]={true,entry.transform,error};
            if(!best.found || error<best.error) best={true,entry.transform,error};
        }
        if(!best.found) return best;
        for(size_t i=0;i<candidateCount;i++) {
            // Do not guess a command when another transform predicts the same
            // layer about as well. Identical commands from many samples are fine.
            if(candidates[i].error<=best.error+0.05
                && matrixError(matrix(candidates[i].transform),matrix(best.transform))>0.04) return {};
        }
        return best;
    }
};
}
