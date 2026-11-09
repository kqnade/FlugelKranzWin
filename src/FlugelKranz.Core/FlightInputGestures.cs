namespace FlugelKranz.Core;

internal enum FlightGesture { None, ToggleMode, ResetSpace }

/// <summary>Tracks one continuous logical D-pad hold. The caller supplies elapsed time.</summary>
internal sealed class FlightInputGestures
{
    private enum DpadHold { None, Left, Right, Both }
    private DpadHold hold;
    private float holdSeconds;
    private bool triggered;

    public FlightGesture Update(InputFrame frame, float elapsedSeconds, FlightMode selectedMode)
    {
        DpadHold current = frame.MotionSuspended ? DpadHold.None : GetDpadHold(frame);
        if (current != hold)
        {
            hold = current;
            holdSeconds = 0;
            triggered = false;
        }
        if (hold == DpadHold.None || triggered) return FlightGesture.None;

        holdSeconds += elapsedSeconds;
        if (holdSeconds >= 1 && hold == DpadHold.Both)
        {
            triggered = true;
            return FlightGesture.ToggleMode;
        }
        if (holdSeconds >= 1 &&
            (selectedMode == FlightMode.InfiniteWalking || frame.HeadTracked && frame.Head.IsValid))
        {
            triggered = true;
            return FlightGesture.ResetSpace;
        }
        return FlightGesture.None;
    }

    private static DpadHold GetDpadHold(InputFrame frame) =>
        (IsHeld(frame.Left), IsHeld(frame.Right)) switch
        {
            (true, true) => DpadHold.Both,
            (true, false) => DpadHold.Left,
            (false, true) => DpadHold.Right,
            _ => DpadHold.None
        };

    private static bool IsHeld(HandSample hand) =>
        hand.IsTracked && hand.Pose.IsValid &&
        float.IsFinite(hand.DpadDown) && hand.DpadDown >= 0.65f;
}
