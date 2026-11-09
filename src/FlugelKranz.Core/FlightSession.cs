namespace FlugelKranz.Core;

/// <summary>
/// Owns flight calculation state on the controller worker; never reads clocks or calls a runtime.
/// PrepareInput precedes release/reset handling, then Step calculates from the last applied offset.
/// If applying a result fails, the owner must discard the session rather than continue stepping it.
/// </summary>
internal sealed class FlightSession
{
    private readonly FreeFlightManipulator freeFlight;
    private readonly InfiniteWalkingManipulator infiniteWalking;
    private readonly FlightInputGestures gestures = new();
    private FlightMode? appliedMode, inputModeOverride;
    private FlightMode selectedMode;
    private InfiniteWalkingTransition? modeTransition;
    private SpaceResetTransition? spaceResetTransition;

    public FlightSession(RigidPose currentOffset)
    {
        freeFlight = new(currentOffset);
        infiniteWalking = new(currentOffset);
    }

    public FlightMode SelectedMode => selectedMode;
    public FlightMode InputMode(FlightMode configuredMode) => inputModeOverride ?? configuredMode;

    public FlightGesture PrepareInput(InputFrame frame, float elapsedSeconds, FlightMode configuredMode)
    {
        if (inputModeOverride == configuredMode) inputModeOverride = null;
        selectedMode = InputMode(configuredMode);
        var gesture = gestures.Update(frame, elapsedSeconds, selectedMode);
        if (gesture == FlightGesture.ToggleMode)
        {
            selectedMode = selectedMode == FlightMode.InfiniteWalking
                ? FlightMode.FreeFlight : FlightMode.InfiniteWalking;
            inputModeOverride = selectedMode;
        }
        return gesture;
    }

    public void ReleaseInputs()
    {
        freeFlight.Release();
        infiniteWalking.Release();
        spaceResetTransition = null;
    }

    public void ResetTo(RigidPose restoredOffset)
    {
        freeFlight.SetOffset(restoredOffset);
        infiniteWalking.SetOffset(restoredOffset);
        modeTransition = null;
        spaceResetTransition = null;
        appliedMode = selectedMode;
    }

    public FlightStepResult Step(InputFrame frame, float elapsedSeconds, FlugelKranzSettings settings,
        bool enabled, RigidPose currentOffset, RigidPose originalOffset, bool spaceResetRequested)
    {
        RigidPose? desiredOffset = null;
        bool spaceResetStarted = false;
        if (enabled && !frame.MotionSuspended)
        {
            if (spaceResetTransition is not null &&
                spaceResetTransition.Mode != selectedMode)
            {
                spaceResetTransition = null;
                freeFlight.SetOffset(currentOffset);
                infiniteWalking.SetOffset(currentOffset);
            }
            if (modeTransition is not null && selectedMode == FlightMode.FreeFlight)
            {
                modeTransition = null;
                appliedMode = FlightMode.FreeFlight;
                freeFlight.SetOffset(currentOffset);
                infiniteWalking.SetOffset(currentOffset);
            }
            if (modeTransition is null && appliedMode != selectedMode)
            {
                if (appliedMode == FlightMode.FreeFlight &&
                    selectedMode == FlightMode.InfiniteWalking)
                {
                    modeTransition = new(currentOffset, originalOffset,
                        freeFlight.GetSingleHandTurnPivot(frame, settings.FreeFlight));
                    spaceResetTransition = null;
                    freeFlight.Release();
                    infiniteWalking.Release();
                }
                else
                {
                    freeFlight.SetOffset(currentOffset);
                    infiniteWalking.SetOffset(currentOffset);
                    appliedMode = selectedMode;
                }
            }

            if (spaceResetRequested && modeTransition is null &&
                spaceResetTransition is null)
            {
                spaceResetTransition = selectedMode == FlightMode.FreeFlight
                    ? SpaceResetTransition.CreateFreeFlight(
                        currentOffset,
                        frame.Head,
                        freeFlight.GetCurrentTurnPivot(frame, settings.FreeFlight))
                    : SpaceResetTransition.CreateInfiniteWalking(
                        currentOffset,
                        originalOffset);
                freeFlight.Release();
                infiniteWalking.Release();
                spaceResetStarted = true;
            }

            RigidPose offset;
            if (modeTransition is not null)
            {
                offset = modeTransition.Advance(elapsedSeconds);
                if (modeTransition.IsComplete)
                {
                    freeFlight.SetOffset(offset);
                    infiniteWalking.SetOffset(offset);
                    appliedMode = FlightMode.InfiniteWalking;
                    modeTransition = null;
                }
            }
            else if (spaceResetTransition is not null)
            {
                offset = spaceResetTransition.Advance(elapsedSeconds);
                if (spaceResetTransition.IsComplete)
                {
                    freeFlight.SetOffset(offset);
                    infiniteWalking.SetOffset(offset);
                    spaceResetTransition = null;
                }
            }
            else
            {
                offset = selectedMode == FlightMode.FreeFlight
                    ? freeFlight.Update(frame, elapsedSeconds, settings.FreeFlight)
                    : infiniteWalking.Update(frame, elapsedSeconds, settings.InfiniteWalking);
            }
            // Use exact equality here: a tolerance would accumulate un-applied substeps as feedback.
            if (offset != currentOffset) desiredOffset = offset;
        }
        else
        {
            bool wasTransitioning = modeTransition is not null || spaceResetTransition is not null;
            modeTransition = null;
            spaceResetTransition = null;
            infiniteWalking.Release();
            if (!enabled && !frame.MotionSuspended && selectedMode == FlightMode.FreeFlight && settings.FreeFlight.DragEnabled)
            {
                if (appliedMode != selectedMode || wasTransitioning)
                    freeFlight.SetOffset(currentOffset);
                appliedMode = selectedMode;
                var dragFrame = frame with
                {
                    PilotAvailable = false,
                    Left = frame.Left with { Turn = 0, DpadDown = 0 },
                    Right = frame.Right with { Turn = 0, DpadDown = 0 }
                };
                var offset = freeFlight.Update(dragFrame, elapsedSeconds, settings.FreeFlight with
                {
                    HeadPilotEnabled = false, DragAccelerationMultiplier = 0,
                    TurnAccelerationMultiplier = 0, InertiaAccelerationBoostEnabled = false
                });
                if (offset != currentOffset) desiredOffset = offset;
            }
            else freeFlight.Release();
        }
        return new(desiredOffset, GetSnapshot(frame, settings, enabled), spaceResetStarted);
    }

    private FlightStateSnapshot GetSnapshot(InputFrame frame, FlugelKranzSettings settings, bool enabled)
    {
        bool transitioning = modeTransition is not null || spaceResetTransition is not null;
        bool dragging = !transitioning && (selectedMode == FlightMode.FreeFlight
            ? freeFlight.IsDragging : infiniteWalking.IsDragging);
        bool turning = !transitioning && (selectedMode == FlightMode.FreeFlight
            ? freeFlight.IsTurning || freeFlight.IsHeadPiloting : infiniteWalking.IsTurning);
        FlightActivity activity = !enabled ? frame.MotionSuspended ? FlightActivity.OffSuspended
            : selectedMode == FlightMode.FreeFlight && settings.FreeFlight.DragEnabled
                ? FlightActivity.OffDrag : FlightActivity.Off
            : frame.MotionSuspended ? FlightActivity.Suspended
            : modeTransition is not null ? FlightActivity.ReturningToWalking
            : spaceResetTransition is not null ? FlightActivity.ResettingSpace
            : !frame.HeadTracked ? FlightActivity.WaitingForHead
            : !frame.Left.IsTracked || !frame.Right.IsTracked ? FlightActivity.WaitingForControllers
            : selectedMode == FlightMode.FreeFlight && settings.FreeFlight.HeadPilotEnabled
                ? freeFlight.IsHeadPiloting ? FlightActivity.HeadPilotOn : FlightActivity.HeadPilotOff
            : FlightActivity.Ready;
        return new(enabled, selectedMode, dragging, turning, activity);
    }
}
