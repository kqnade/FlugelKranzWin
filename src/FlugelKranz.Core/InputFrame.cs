namespace FlugelKranz.Core;

public readonly record struct HandSample(
    RigidPose Pose,
    float Drag,
    float Turn,
    float DpadDown,
    bool IsTracked)
{
    public bool TrackpadForceActive { get; init; }
    public float TrackpadForce { get; init; }
}

public readonly record struct InputFrame(
    RigidPose Head,
    bool HeadTracked,
    HandSample Left,
    HandSample Right)
{
    public System.Numerics.Vector2 PilotStick { get; init; }
    public bool PilotHeld { get; init; }
    public bool PilotAvailable { get; init; }
    public bool MotionSuspended { get; init; }
}
