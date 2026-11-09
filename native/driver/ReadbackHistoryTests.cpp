#include "PoseReadbackHistory.h"
#include <cstdlib>
#include <cstdio>

namespace {
void require(bool value) {if(!value) std::abort();}
vr::DriverPose_t bodyPose() {
    vr::DriverPose_t p{};
    p.qRotation.w=p.qWorldFromDriverRotation.w=p.qDriverFromHeadRotation.w=1;
    p.poseIsValid=p.deviceIsConnected=true;
    return p;
}
void translationPrediction(float prediction,double queryTime,double offset,bool newerReturned) {
    flight::PoseReadbackHistory history;
    auto old=bodyPose();old.vecVelocity[0]=1;old.poseTimeOffset=offset;
    auto newer=old;newer.vecPosition[0]=0.01;
    const double target=(queryTime-1000)/1000+prediction-offset;
    const flight::Pose a{0,0,0,1,3,0,0},b{0,0,0,1,3+target*0.95,0,0};
    history.CommitForward(0,history.BeginForward(0,a,old,1000),a);
    const auto committed=history.CaptureCommitted();
    history.CommitForward(0,history.BeginForward(0,b,newer,1010),b);
    vr::TrackedDevicePose_t pose{};
    pose.mDeviceToAbsoluteTracking=flight::matrix({0,0,0,1,(newerReturned?b.px:a.px)+target,0,0});
    pose.vVelocity.v[0]=1;
    flight::PoseReadbackHistory::ReadbackTally tally;
    history.readbackByGeneration(&pose,1,committed,queryTime,prediction,tally);
    require(std::abs(pose.mDeviceToAbsoluteTracking.m[0][3]-target)<1e-6);
    require(pose.vVelocity.v[0]==1 && tally.window==1);
}
void calibratedAngularPrediction() {
    flight::PoseReadbackHistory history;
    const double h=std::sqrt(0.5),dt=0.025;
    auto p=bodyPose();
    p.poseTimeOffset=-0.005;
    p.qRotation={h,h,0,0};
    p.vecPosition[0]=1;p.vecVelocity[0]=1;p.vecAngularVelocity[1]=1;
    p.qWorldFromDriverRotation={h,0,h,0};
    p.vecWorldFromDriverTranslation[0]=2;p.vecWorldFromDriverTranslation[1]=3;p.vecWorldFromDriverTranslation[2]=4;
    p.vecDriverFromHeadTranslation[0]=0.3;p.qDriverFromHeadRotation={h,h,0,0};
    // Analytic prediction: Y motion * initial X90, then Y90 world and X90 head calibration.
    // X and Y do not commute: reversing the angular update's product must fail this test.
    const double c=std::cos(dt/2),s=std::sin(dt/2);
    const flight::Pose expected{h*(c-s),0,-h*(c+s),0,
        2-0.3*std::sin(dt),3,4-(1+dt+0.3*std::cos(dt))};
    const flight::Pose a{0,0,0,1,0,0,0};
    const flight::Pose b{0,std::sin(dt/2),0,std::cos(dt/2),
        2-(2*std::cos(dt)+2.7*std::sin(dt)),0,2.7-(-2*std::sin(dt)+2.7*std::cos(dt))};
    history.CommitForward(0,history.BeginForward(0,a,p,1000),a);
    const auto committed=history.CaptureCommitted();
    // Both arrivals describe the same sample time despite different poseTimeOffsets.
    p.poseTimeOffset=-0.015;
    history.CommitForward(0,history.BeginForward(0,b,p,1010),b);
    vr::TrackedDevicePose_t pose{};
    pose.mDeviceToAbsoluteTracking=flight::matrix(expected);
    const auto returned=pose.mDeviceToAbsoluteTracking;
    flight::PoseReadbackHistory::ReadbackTally tally;
    history.readbackByGeneration(&pose,1,committed,1000,0.02f,tally);
    require(flight::matrixError(pose.mDeviceToAbsoluteTracking,returned)<1e-8);
    require(tally.pickedPrevious==1 && tally.residualMax<1e-6);
}
}

int main() {
    auto require=[](bool value){if(!value) std::abort();};
    flight::PoseReadbackHistory history;
    const flight::Pose transform{0,0,0,1,2,3,4};
    for(uint32_t device:{0u,63u}) {
        const auto generation=history.BeginForward(device,transform,vr::DriverPose_t{},0);
        require(generation==1 && history.CaptureCommitted()[device]==0);
        require(history.Inspect(device).forwarding);
        history.CommitForward(device,generation,transform);
        require(history.CaptureCommitted()[device]==1);
        require(!history.Inspect(device).forwarding && history.Inspect(device).applied.px==2);
    }
    history.Reset();
    for(uint32_t device=0;device<64;device++) {
        const auto snapshot=history.Inspect(device);
        require(flight::isIdentity(snapshot.applied) && flight::isIdentity(snapshot.pending));
        require(!snapshot.forwarding && !snapshot.reordered && snapshot.committed==0);
    }
    require(history.BeginForward(63,transform,vr::DriverPose_t{},0)==1);
    for(bool newerReturned:{false,true}) {
        translationPrediction(0.02f,1000,0,newerReturned);
        translationPrediction(-0.02f,1000,0,newerReturned);
        translationPrediction(0,1020,0,newerReturned); // sample age without requested prediction
        translationPrediction(0,1000,-0.02,newerReturned); // driver timestamp offset
        translationPrediction(0.02f,1005,-0.005,newerReturned);
    }
    calibratedAngularPrediction();
    std::puts("readback history: requested time, sample age, pose offset, old/new generation, calibrated angular motion: passed");
}
