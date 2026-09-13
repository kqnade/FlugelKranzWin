#include "Transform.h"
#include <cstdio>
#include <cstdlib>
void check(bool ok) {if(!ok){std::fputs("Transform test failed\n",stderr);std::exit(1);}}
int main() {
    vr::DriverPose_t p{};p.qRotation.w=p.qWorldFromDriverRotation.w=p.qDriverFromHeadRotation.w=1;
    p.vecPosition[0]=1;p.vecWorldFromDriverTranslation[1]=2;p.vecVelocity[0]=3;
    for(int axis=0;axis<3;axis++) {
        flight::Pose t;t.w=std::cos(0.3);t.pz=4;
        if(axis==0)t.x=std::sin(0.3);else if(axis==1)t.y=std::sin(0.3);else t.z=std::sin(0.3);
        auto out=flight::apply(p,t);
        check(std::abs(out.qWorldFromDriverRotation.w-t.w)<1e-10);
        check(out.vecVelocity[0]==3 && out.vecPosition[0]==1);
        auto actual=flight::physical(out), original=flight::physical(p);
        double before[]={original.px,original.py,original.pz},after[3];
        flight::rotate({t.w,t.x,t.y,t.z},before,after);
        check(std::abs(actual.px-after[0])<1e-10 && std::abs(actual.py-after[1])<1e-10 && std::abs(actual.pz-after[2]-4)<1e-10);
        check(flight::valid(actual));
    }
    check(!flight::valid({0,0,0,0,0,0,0}));
    std::puts("XYZ composition, physical snapshots, velocity preservation, validation: passed");
}
