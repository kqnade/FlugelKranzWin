namespace FlugelKranz.Core;

internal enum FlightActivity
{
    Off, OffDrag, OffSuspended, Suspended, ReturningToWalking, ResettingSpace,
    WaitingForHead, WaitingForControllers, HeadPilotOn, HeadPilotOff, Ready
}

internal readonly record struct FlightStateSnapshot(
    bool Enabled, FlightMode Mode, bool Dragging, bool Turning, FlightActivity Activity);

// Offset is a requested calculation result, not confirmation of a successful runtime write.
internal readonly record struct FlightStepResult(
    RigidPose? Offset, FlightStateSnapshot State, bool SpaceResetStarted);
