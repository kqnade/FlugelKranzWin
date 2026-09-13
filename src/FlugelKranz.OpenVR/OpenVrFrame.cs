using FlugelKranz.Core;
using Valve.VR;

namespace FlugelKranz.OpenVR;

/// <summary>All device poses must come from the same IVRSystem raw snapshot.</summary>
public static class OpenVrFrame
{
    public static HandSample Hand(TrackedDevicePose_t[] poses, uint device,
        InputDigitalActionData_t drag, InputDigitalActionData_t turn, InputDigitalActionData_t reset)
    {
        if (device >= poses.Length) return new(RigidPose.Identity, 0, 0, 0, false);
        var pose = poses[device];
        bool tracked = pose.bDeviceIsConnected && pose.bPoseIsValid;
        return new(tracked ? OpenVrPose.FromMatrix(pose.mDeviceToAbsoluteTracking) : RigidPose.Identity,
            tracked && drag.bActive && drag.bState ? 1 : 0,
            tracked && turn.bActive && turn.bState ? 1 : 0,
            tracked && reset.bActive && reset.bState ? 1 : 0, tracked);
    }
}
