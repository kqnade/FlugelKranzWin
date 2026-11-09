#pragma once
#include <array>
#include <atomic>
#include <mutex>
#include <optional>
#include <algorithm>
#include "FrameHistory.h"

namespace flight {
// Begin/Commit bracket forwarding without holding this lock across the original callback.
class PoseReadbackHistory {
// appliedGate is held only for a few assignments before and after a pose update is
// forwarded and for one array copy by the readback hook. The readback must not take
// `gate`: SlimeVR polls raw poses every ~2 ms, pose updates hold `gate`, and the HMD's
// own updates then queue behind them. Measured on Quest 2 + VD + SlimeVR, that left the
// render correction using HMD samples 47 ms or older in 72% of flying frames
// (0.3% before the hook), which is visible as judder in the headset.
std::mutex appliedGate;
std::array<flight::Pose,64> lastApplied{}; // protected by appliedGate
// While updated() forwards a pose, the readback cannot tell whether vrserver already holds
// it, so it also gets the transform that pose carries and its physical position.
std::array<flight::Pose,64> pendingApplied{}; // protected by appliedGate
std::array<std::array<double,3>,64> pendingPhysical{}; // protected by appliedGate
std::array<bool,64> forwarding{}; // protected by appliedGate
struct Forward {uint64_t generation=0;flight::Pose transform;vr::DriverPose_t original{};double received=0;};
static constexpr size_t forwardRing=8;
std::array<std::array<Forward,forwardRing>,64> recentForwards{}; // protected by appliedGate
std::array<uint64_t,64> startedGeneration{}; // protected by appliedGate
std::array<std::atomic<uint64_t>,64> committedGeneration{}; // written under appliedGate, read lock-free
std::array<uint32_t,64> forwardsInFlight{}; // protected by appliedGate
std::array<bool,64> reordered{}; // protected by appliedGate
public:
struct ReadbackTally {uint64_t window=0,pickedPending=0,pickedPrevious=0,ambiguous=0,overflow=0;double residualMax=0;};
void tallyPick(ReadbackTally& tally,const flight::ReadbackPick& pick,bool different) {
    tally.window++;
    (pick.pending?tally.pickedPending:tally.pickedPrevious)++;
    // Two different candidates score within a factor of two: the pick is a guess.
    if(different && pick.farther<2*pick.nearer) tally.ambiguous++;
    tally.residualMax=std::max(tally.residualMax,pick.residual);
}
// Pair readbacks with the generation observed before entering vrserver. A device whose committed generation is the same as before the
// call, with no forward in flight and no out-of-order finish, gets the committed transform:
// nothing reached vrserver in between. Any other device gets the best-scoring forward from
// the one committed before the call to the newest started.
void readbackByGeneration(vr::TrackedDevicePose_t* poses,uint32_t count,const std::array<uint64_t,64>& committedBefore,
    double queryTime,float prediction,ReadbackTally& tally) {
    struct Unsettled {uint32_t device=0;size_t count=0;std::array<Forward,forwardRing+1> candidates{};};
    std::array<flight::Pose,64> applied;
    // Keep capacity for every device, but initialize pose storage only for unsettled ones.
    std::array<std::optional<Unsettled>,64> unsettled;
    size_t unsettledCount=0;
    const uint32_t devices=std::min<uint32_t>(count,64);
    {
        std::lock_guard<std::mutex> lock(appliedGate);
        for(uint32_t device=0;device<devices;device++) {
            applied[device]=lastApplied[device];
            const uint64_t committed=committedGeneration[device].load(std::memory_order_relaxed);
            if(committed==committedBefore[device] && !forwardsInFlight[device] && !reordered[device]) continue;
            if(unsettledCount==unsettled.size()) {tally.overflow++;continue;}
            auto& entry=unsettled[unsettledCount++].emplace();
            entry.device=device;entry.count=0;
            const uint64_t started=startedGeneration[device];
            uint64_t from=committedBefore[device];
            // Overlapping forwards of one device can reach vrserver in either order, so an
            // older one than the committed may be what it holds: consider every recent one.
            if(reordered[device] || forwardsInFlight[device]>0) from=started>=forwardRing?started-forwardRing+1:0;
            else if(started-from>=forwardRing) {from=started-forwardRing+1;tally.overflow++;} // the oldest are gone
            for(uint64_t generation=from;generation<=started;generation++) {
                if(generation==0) {entry.candidates[entry.count++]={};continue;} // before any forward
                const auto& forward=recentForwards[device][generation%forwardRing];
                if(forward.generation==generation) entry.candidates[entry.count++]=forward;
            }
            // Generation 0 has no sample; approximate it with the first update's motion.
            if(entry.count>1 && from==0) {
                entry.candidates[0]=entry.candidates[1];
                entry.candidates[0].transform=flight::Pose{};
            }
            if(!entry.count || (entry.count==1 && from==0)) entry.count=0; // nothing to score against
        }
    }
    std::array<bool,64> settled;settled.fill(true);
    for(size_t i=0;i<unsettledCount;i++) {
        auto& entry=*unsettled[i];
        if(!entry.count) continue;
        std::array<flight::ReadbackCandidate,forwardRing+1> candidates;
        size_t candidateCount=0;
        for(size_t j=0;j<entry.count;j++) {
            const auto& forward=entry.candidates[j];
            // Predict all candidates to the same requested time, outside appliedGate.
            const double dt=(queryTime-forward.received)/1000+prediction-forward.original.poseTimeOffset;
            if(!std::isfinite(dt)) continue;
            const auto body=flight::predictReadbackBody(forward.original,dt);
            if(flight::valid(body)) candidates[candidateCount++]={forward.transform,body};
        }
        if(!candidateCount) continue;
        settled[entry.device]=false;
        const auto pick=flight::pickReadback(poses[entry.device].mDeviceToAbsoluteTracking,candidates.data(),candidateCount);
        tallyPick(tally,pick,pick.farther>0);
        flight::removeTransform(poses[entry.device],pick.transform);
    }
    for(uint32_t device=0;device<devices;device++) if(settled[device]) flight::removeTransform(poses[device],applied[device]);
}
    std::array<uint64_t,64> CaptureCommitted() const {
        std::array<uint64_t,64> result;
        for(uint32_t device=0;device<64;device++) result[device]=committedGeneration[device].load(std::memory_order_acquire);
        return result;
    }
    uint64_t BeginForward(uint32_t device,Pose transform,const vr::DriverPose_t& original,double received) {
        const auto body=physical(original);
        std::lock_guard<std::mutex> lock(appliedGate);
        pendingApplied[device]=transform;
        pendingPhysical[device]={body.px,body.py,body.pz};
        forwarding[device]=true;
        const auto generation=++startedGeneration[device];
        recentForwards[device][generation%forwardRing]={generation,transform,original,received};
        forwardsInFlight[device]++;
        return generation;
    }
    void CommitForward(uint32_t device,uint64_t generation,Pose transform) {
        std::lock_guard<std::mutex> lock(appliedGate);
        forwardsInFlight[device]--;
        if(generation>committedGeneration[device].load(std::memory_order_relaxed)) {
            committedGeneration[device].store(generation,std::memory_order_release);
            reordered[device]=false;
            lastApplied[device]=transform;
        } else reordered[device]=true;
        forwarding[device]=false;
    }
    // Only after all callbacks have drained.
    void Reset() {
        std::lock_guard<std::mutex> lock(appliedGate);
        lastApplied.fill(Pose{});pendingApplied.fill(Pose{});
        pendingPhysical.fill({});forwarding.fill(false);
        for(auto& forwards:recentForwards) forwards.fill(Forward{});
        startedGeneration.fill(0);forwardsInFlight.fill(0);reordered.fill(false);
        for(auto& committed:committedGeneration) committed.store(0);
    }
    struct Snapshot {Pose applied,pending;std::array<double,3> physical;bool forwarding,reordered;uint64_t committed;};
    Snapshot Inspect(uint32_t device) {
        std::lock_guard<std::mutex> lock(appliedGate);
        return {lastApplied[device],pendingApplied[device],pendingPhysical[device],forwarding[device],reordered[device],committedGeneration[device].load()};
    }
};
}
