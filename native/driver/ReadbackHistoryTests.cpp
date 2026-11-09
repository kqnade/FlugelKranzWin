#include "PoseReadbackHistory.h"
#include <cstdlib>

int main() {
    auto require=[](bool value){if(!value) std::abort();};
    flight::PoseReadbackHistory history;
    const flight::Pose transform{0,0,0,1,2,3,4};
    for(uint32_t device:{0u,63u}) {
        const auto generation=history.BeginForward(device,transform,flight::Pose{});
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
    require(history.BeginForward(63,transform,flight::Pose{})==1);
}
