#pragma once
#include <mutex>
#include <chrono>
#include <cstring>
#include "FrameHistory.h"

namespace flight {
inline double frameTimeMs() {
    return std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
// Owns the render-pose history; never holds its lock while forwarding a layer.
class FrameCorrection {
    FrameHistory frameHistory;
    std::mutex historyGate;
public:
    using Layer=vr::IVRDriverDirectModeComponent::SubmitLayerPerEye_t;
    bool correctFramePose=false,timeBasedFramePose=false; // set before hook installation
    struct Result {FrameMatch match;PhysicalFrame physicalLeft;bool identityBypass,usedTimed;};
    Result Prepare(const Layer(&eyes)[2],Layer(&corrected)[2]) {
        const auto& pose=eyes[0].mHmdPose;
        const auto prediction=eyes[0].flHmdPosePredictionTimeInSecondsFromNow;
    flight::FrameMatch match;
    flight::PhysicalFrame physicalLeft,physicalRight;
    bool identityBypass=false,usedTimed=false;
    if(correctFramePose) {
        std::lock_guard<std::mutex> lock(historyGate);
        auto frameNow=frameTimeMs();
        identityBypass=frameHistory.canBypassCorrection(frameNow);
        if(identityBypass) {
            // Forward the original layer below without copying or predicting it.
        } else if(timeBasedFramePose) {
            match=frameHistory.heldMatch(frameNow,prediction,pose);
            auto right=frameHistory.heldMatch(frameNow,eyes[1].flHmdPosePredictionTimeInSecondsFromNow,eyes[1].mHmdPose);
            if(!right.found) match.found=false;
            if(!match.found) {
            usedTimed=true;
            physicalLeft=frameHistory.physicalAt(frameNow,prediction);
            physicalRight=frameHistory.physicalAt(frameNow,eyes[1].flHmdPosePredictionTimeInSecondsFromNow);
            match.found=physicalLeft.found && physicalRight.found;
            match.error=-1; // This path does not perform a virtual-pose fit.
            match.held=false;
            }
        } else {
        match=frameHistory.match(frameNow,prediction,pose);
        // Both eyes must identify the same transform; otherwise forward neither.
        auto right=frameHistory.match(frameNow,eyes[1].flHmdPosePredictionTimeInSecondsFromNow,eyes[1].mHmdPose);
        if(!right.found || (match.found && flight::matrixError(flight::matrix(match.transform),flight::matrix(right.transform))>0.04)) match.found=false;
        }
    }
    if(match.found) {
        std::memcpy(corrected,eyes,sizeof(corrected));
        if(usedTimed) {
            corrected[0].mHmdPose=flight::matrix(physicalLeft.pose);
            corrected[1].mHmdPose=flight::matrix(physicalRight.pose);
        } else for(auto& eye:corrected) eye.mHmdPose=flight::removeTransform(eye.mHmdPose,match.transform);

    }
        return {match,physicalLeft,identityBypass,usedTimed};
    }
    void Add(double received,const vr::DriverPose_t& output,Pose command,const vr::DriverPose_t* input=nullptr) {
        std::lock_guard<std::mutex> lock(historyGate);
        frameHistory.add(received,output,command,input);
    }
    void ClearAfterDrain() {frameHistory.clear();}
};
}
