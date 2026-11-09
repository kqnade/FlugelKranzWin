using System.Numerics;
using FlugelKranz.Core;
using Xunit;

namespace FlugelKranz.Tests;

public class FlightInputGesturesTests
{
    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]
    public void FiresAtOneSecondAndOnlyOncePerHold(float left, float right, bool toggle)
    {
        var gestures = new FlightInputGestures();
        var frame = Frame(left, right);
        Assert.Equal(FlightGesture.None, gestures.Update(frame, 0.75f, FlightMode.FreeFlight));
        Assert.Equal(toggle ? FlightGesture.ToggleMode : FlightGesture.ResetSpace,
            gestures.Update(frame, 0.25f, FlightMode.FreeFlight));
        Assert.Equal(FlightGesture.None, gestures.Update(frame, 5, FlightMode.FreeFlight));
        gestures.Update(Frame(0, 0), 0.01f, FlightMode.FreeFlight);
        Assert.Equal(toggle ? FlightGesture.ToggleMode : FlightGesture.ResetSpace,
            gestures.Update(frame, 1, FlightMode.FreeFlight));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    public void ChangingHoldSourceStartsANewTimer(float left, float right)
    {
        var gestures = new FlightInputGestures();
        gestures.Update(Frame(1, 0), 0.75f, FlightMode.FreeFlight);
        Assert.Equal(FlightGesture.None, gestures.Update(Frame(left, right), 0.25f, FlightMode.FreeFlight));
    }

    [Fact]
    public void DashboardClearsElapsedHoldTime()
    {
        var gestures = new FlightInputGestures();
        gestures.Update(Frame(1, 1), 0.75f, FlightMode.FreeFlight);
        Assert.Equal(FlightGesture.None, gestures.Update(Frame(1, 1) with { MotionSuspended = true }, 2, FlightMode.FreeFlight));
        Assert.Equal(FlightGesture.None, gestures.Update(Frame(1, 1), 0.25f, FlightMode.FreeFlight));
        Assert.Equal(FlightGesture.ToggleMode, gestures.Update(Frame(1, 1), 0.75f, FlightMode.FreeFlight));
    }

    [Theory]
    [InlineData(0.649f, false)]
    [InlineData(0.65f, true)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void LogicalButtonRequiresFiniteThreshold(float value, bool held)
    {
        var gestures = new FlightInputGestures();
        Assert.Equal(held ? FlightGesture.ResetSpace : FlightGesture.None,
            gestures.Update(Frame(value, 0), 1, FlightMode.FreeFlight));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnusableHandDoesNotCountAsHeld(bool invalidPose)
    {
        var gestures = new FlightInputGestures();
        var frame = Frame(1, 0);
        frame = frame with { Left = frame.Left with
        {
            IsTracked = invalidPose,
            Pose = invalidPose ? new(Quaternion.Identity, new(float.NaN, 0, 0)) : RigidPose.Identity
        } };
        Assert.Equal(FlightGesture.None, gestures.Update(frame, 1, FlightMode.FreeFlight));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreeFlightResetWaitsForUsableHeadWithoutConsumingGesture(bool invalidPose)
    {
        var gestures = new FlightInputGestures();
        var frame = Frame(1, 0) with
        {
            HeadTracked = invalidPose,
            Head = invalidPose ? new(Quaternion.Identity, new(float.NaN, 0, 0)) : RigidPose.Identity
        };
        Assert.Equal(FlightGesture.None, gestures.Update(frame, 1, FlightMode.FreeFlight));
        Assert.Equal(FlightGesture.ResetSpace, gestures.Update(Frame(1, 0), 0.01f, FlightMode.FreeFlight));
    }

    [Fact]
    public void WalkingResetDoesNotRequireHeadTracking()
    {
        Assert.Equal(FlightGesture.ResetSpace, new FlightInputGestures().Update(
            Frame(1, 0) with { HeadTracked = false }, 1, FlightMode.InfiniteWalking));
    }

    [Fact]
    public void ModeToggleTakesPriorityEvenWithoutHeadTracking()
    {
        Assert.Equal(FlightGesture.ToggleMode, new FlightInputGestures().Update(
            Frame(1, 1) with { HeadTracked = false }, 1, FlightMode.FreeFlight));
    }

    private static InputFrame Frame(float left, float right) => new(RigidPose.Identity, true,
        new(RigidPose.Identity, 0, 0, left, true), new(RigidPose.Identity, 0, 0, right, true));
}
