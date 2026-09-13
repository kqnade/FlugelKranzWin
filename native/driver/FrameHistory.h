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
class FrameHistory {
    struct Entry {double tick=0;vr::DriverPose_t output{};Pose transform;};
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
    void add(double tick,const vr::DriverPose_t& output,Pose transform) {
        if(!output.poseIsValid || !output.deviceIsConnected || !valid(transform)
            || !valid(physical(output)) || !std::isfinite(tick)) {clear();return;}
        if(!count || tick<lastTick || tick-lastTick>100 || !same(transform,heldTransform)) {
            heldSince=tick;heldTransform=transform;
        }
        lastTick=tick;
        entries[next]={tick,output,transform};next=(next+1)%entries.size();count=std::min(count+1,entries.size());
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
