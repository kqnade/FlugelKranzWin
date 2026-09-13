using System.Numerics;
using FlugelKranz.Core;
using FlugelKranz.OpenVR;
using Xunit;

namespace FlugelKranz.Tests;

public class HeadPilotTests
{
    private static InputFrame Frame => new(RigidPose.Identity, true,
        new(RigidPose.Identity, 0, 0, 0, true), new(RigidPose.Identity, 0, 0, 0, true)) { PilotAvailable = true };
    private static readonly FlightMotionSettings Settings = FlightMotionSettings.Default with
    { HeadPilotEnabled = true, DragSmoothSeconds = 0, TurnSmoothSeconds = 0, InertiaDecelerationPerSecond = 0 };
    private static void Click(HeadPilot pilot, InputFrame frame)
    {
        pilot.Read(frame with { PilotHeld = true }, RigidPose.Identity, 0.01f);
        for (int i = 0; i < 4; i++) pilot.Read(frame, RigidPose.Identity, 0.1f);
    }
    private static HeadPilot ActivePilot()
    {
        var pilot = new HeadPilot();
        pilot.Read(Frame, RigidPose.Identity, 0.01f);
        Click(pilot, Frame);
        return pilot;
    }

    [Fact]
    public void ShortPressTogglesOnReleaseWithoutDelay()
    {
        var pilot = new HeadPilot();
        pilot.Read(Frame, RigidPose.Identity, 0.01f);
        pilot.Read(Frame with { PilotHeld = true }, RigidPose.Identity, 0.01f);
        Assert.False(pilot.Active);
        pilot.Read(Frame, RigidPose.Identity, 0.01f);
        Assert.True(pilot.Active);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongPressResetsInertiaWithoutChangingToggle(bool active)
    {
        var pilot = active ? ActivePilot() : new HeadPilot();
        pilot.Read(Frame, RigidPose.Identity, 0.01f);
        pilot.Read(Frame with { PilotHeld = true }, RigidPose.Identity, 0.01f);
        for (int i = 0; i < 4; i++) pilot.Read(Frame with { PilotHeld = true }, RigidPose.Identity, 0.1f);
        var command = pilot.Read(Frame with { PilotHeld = true }, RigidPose.Identity, 0.1f);
        for (int i = 0; i < 4; i++) pilot.Read(Frame, RigidPose.Identity, 0.1f);
        Assert.True(command.ResetInertia);
        Assert.Equal(active, pilot.Active);
    }
    [Fact]
    public void NextSingleClickStopsHeadRotation()
    {
        var pilot = ActivePilot();
        Click(pilot, Frame);
        Assert.False(pilot.Active);
    }
    [Theory]
    [InlineData(1,0,0)]
    [InlineData(0,1,0)]
    [InlineData(0,0,1)]
    public void PhysicalTiltCommandsContinuousBoundedRotation(float x, float y, float z)
    {
        var pilot = ActivePilot();
        var axis = new Vector3(x,y,z);
        var tilted = Frame with { Head = new(Quaternion.CreateFromAxisAngle(axis, 0.5f), Vector3.Zero) };
        var first = pilot.Read(tilted, RigidPose.Identity, 0.01f).AngularVelocity;
        var next = pilot.Read(tilted, RigidPose.Identity, 0.01f).AngularVelocity;
        Assert.True(Vector3.Dot(axis, first) > 1.5f);
        Assert.Equal(first, next);
        Assert.InRange(first.Length(), 0, MathF.PI / 2 + 0.00001f);
    }
    [Fact]
    public void NeutralHasDeadzoneAndUsesActivationHeadPose()
    {
        var pilot = new HeadPilot();
        var tilted = Frame with { Head = new(Quaternion.CreateFromYawPitchRoll(1,0.5f,0.2f), Vector3.Zero) };
        pilot.Read(tilted, RigidPose.Identity, 0.01f);
        Click(pilot, tilted);
        tilted = tilted with { Head = tilted.Head with { Orientation = Quaternion.Normalize(tilted.Head.Orientation * Quaternion.CreateFromAxisAngle(Vector3.UnitX,0.02f)) } };
        Assert.Equal(Vector3.Zero, pilot.Read(tilted, RigidPose.Identity, 0.01f).AngularVelocity);
    }
    [Fact]
    public void ForwardThrustUsesVirtualViewIncludingPitch()
    {
        var pilot = new HeadPilot();
        pilot.Read(Frame, RigidPose.Identity, 0.01f);
        var offset = new RigidPose(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI/2), Vector3.Zero);
        var acceleration = pilot.Read(Frame with { PilotStick = Vector2.UnitY }, offset, 0.01f).Acceleration;
        Assert.True(Vector3.Distance(new(0,14,0), acceleration) < 0.00001f);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnavailableSuspendedOrLostTrackingDisarms(int reason)
    {
        var pilot = ActivePilot();
        var frame = reason switch
        {
            0 => Frame with { PilotAvailable = false },
            1 => Frame with { MotionSuspended = true },
            _ => Frame with { HeadTracked = false }
        };
        pilot.Read(frame, RigidPose.Identity, 0.01f);
        Assert.False(pilot.Active);
        Assert.Equal(Vector3.Zero, pilot.Read(Frame with { PilotStick = Vector2.UnitY }, RigidPose.Identity, 0.01f).Acceleration);
    }
    [Fact]
    public void HorizontalStickInputIsIgnoredForArmingAndThrust()
    {
        var pilot = new HeadPilot();
        pilot.Read(Frame with { PilotStick = Vector2.UnitX }, RigidPose.Identity, 0.01f);
        var sideways = pilot.Read(Frame with { PilotStick = Vector2.UnitX }, RigidPose.Identity, 0.01f);
        Assert.Equal(Vector3.Zero, sideways.Acceleration);
        Assert.True(pilot.Read(Frame with { PilotStick = new(1,1) }, RigidPose.Identity, 0.01f).Acceleration.Z < -13.9f);
    }
    [Fact]
    public void BackwardStickReversesThrust()
    {
        var pilot = new HeadPilot();
        pilot.Read(Frame, RigidPose.Identity, 0.01f);
        Assert.Equal(new Vector3(0,0,14), pilot.Read(Frame with { PilotStick = -Vector2.UnitY }, RigidPose.Identity, 0.01f).Acceleration);
    }

    [Fact]
    public void InvalidAnalogInputCannotGenerateMotion()
    {
        var pilot = ActivePilot();
        Assert.Equal(default, pilot.Read(Frame with { PilotStick = new(float.NaN,0) }, RigidPose.Identity, 0.01f));
    }
    [Theory]
    [InlineData(false,false,0)]
    [InlineData(false,true,0)]
    [InlineData(true,true,0)]
    [InlineData(true,false,0x01000000)]
    public void OnlyActiveFlightOutsideDashboardHasGlobalPriority(bool requested, bool dashboard, int expected)
        => Assert.Equal(expected, PilotInputGate.Priority(requested,dashboard));

    [Fact]
    public void DashboardRequiresNeutralAndDoesNotResumeAutomatically()
    {
        var gate = new PilotInputGate();
        Assert.True(gate.Update(true,false,false));
        Assert.True(gate.Update(true,false,true));
        Assert.False(gate.Update(true,false,false));
        Assert.True(gate.Update(true,true,true));
        Assert.True(gate.Update(true,false,false));
        Assert.True(gate.Update(true,false,true));
        Assert.False(gate.Update(true,false,false));
    }
    [Fact]
    public void OffReleasesGateAndRequiresNeutralOnNextActivation()
    {
        var gate = new PilotInputGate();
        gate.Update(true,false,true);
        Assert.False(gate.Update(false,false,false));
        Assert.True(gate.Update(true,false,false));
    }
    [Fact]
    public void LongPressStopsExistingTranslationWithoutRestoringOffset()
    {
        var motion = new FreeFlightManipulator(RigidPose.Identity);
        motion.Update(Frame,0.1f,Settings);
        motion.Update(Frame with { PilotStick = Vector2.UnitY },0.1f,Settings);
        Assert.True(motion.HasLinearInertia);
        motion.Update(Frame with { PilotHeld = true },0.01f,Settings);
        for (int i = 0; i < 4; i++) motion.Update(Frame with { PilotHeld = true },0.1f,Settings);
        var before = motion.Offset;
        var after = motion.Update(Frame with { PilotHeld = true },0.1f,Settings);
        Assert.Equal(before,after);
        Assert.False(motion.HasLinearInertia);
    }
    [Fact]
    public void HeldPhysicalTiltKeepsRotatingAroundHeadWithoutFeedback()
    {
        var motion = new FreeFlightManipulator(RigidPose.Identity);
        var frame = Frame with { Head = new(Quaternion.Identity, new(0,1.7f,0)) };
        motion.Update(frame,0.01f,Settings);
        motion.Update(frame with { PilotHeld = true },0.01f,Settings);
        motion.Update(frame,0.01f,Settings);
        frame = frame with { Head = frame.Head with { Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX,-0.5f) } };
        var first = motion.Update(frame,0.1f,Settings);
        var second = motion.Update(frame,0.1f,Settings);
        Assert.False(first.NearlyEquals(second));
        Assert.True(Vector3.Distance(frame.Head.Position, second.Transform(frame.Head.Position)) < 0.00001f);
        Assert.InRange(2*MathF.Acos(second.Orientation.W),0.31f,0.32f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(10)]
    public void FullThrustReachesSpeedCapWithoutNaturalDamping(float damping)
    {
        var motion = new FreeFlightManipulator(RigidPose.Identity);
        var settings = Settings with { InertiaDecelerationPerSecond = damping };
        motion.Update(Frame,0.01f,settings);
        var thrust = Frame with { PilotStick = Vector2.UnitY };
        for(int i=0;i<100;i++) motion.Update(thrust,0.01f,settings);
        var before = motion.Offset;
        var after = motion.Update(thrust,0.01f,settings);
        Assert.InRange(Vector3.Distance(before.Position,after.Position)/0.01f,11.999f,12.001f);
    }

    [Fact]
    public void ReleasingThrustResumesConfiguredDamping()
    {
        var motion = new FreeFlightManipulator(RigidPose.Identity);
        var settings = Settings with { InertiaDecelerationPerSecond = 2, DecelerationExemptionEnabled = false };
        motion.Update(Frame,0.01f,settings);
        for(int i=0;i<100;i++) motion.Update(Frame with { PilotStick = Vector2.UnitY },0.01f,settings);
        for(int i=0;i<100;i++) motion.Update(Frame,0.01f,settings);
        var before = motion.Offset;
        var after = motion.Update(Frame,0.01f,settings);
        Assert.InRange(Vector3.Distance(before.Position,after.Position)/0.01f,1.62f,1.63f);
    }

    [Fact]
    public void HeadPilotAndDragAreEnabledByDefault()
    {
        Assert.True(FlightMotionSettings.Default.HeadPilotEnabled);
        Assert.True(FlightMotionSettings.Default.DragEnabled);
    }

    [Fact]
    public void DisablingDragCancelsGrabWithoutReleaseBoostAndRequiresRearm()
    {
        var motion = new FreeFlightManipulator(RigidPose.Identity);
        motion.Update(Frame,0.01f,Settings);
        var grip = Frame with { Left = Frame.Left with { Drag = 1 } };
        motion.Update(grip,0.01f,Settings);
        grip = grip with { Left = grip.Left with { Pose = new(Quaternion.Identity,Vector3.UnitX) } };
        motion.Update(grip,0.01f,Settings);
        var before = motion.Offset;
        motion.Update(grip,0.01f,Settings with { DragEnabled = false });
        Assert.False(motion.IsDragging);
        Assert.False(motion.HasLinearInertia);
        Assert.Equal(before,motion.Offset);
        motion.Update(grip,0.01f,Settings);
        Assert.False(motion.IsDragging);
        motion.Update(grip with { Left = grip.Left with { Drag = 0 } },0.01f,Settings);
        motion.Update(grip,0.01f,Settings);
        Assert.True(motion.IsDragging);
    }

    [Fact]
    public void DisablingDragStillAllowsStickThrustWithGripHeld()
    {
        var motion = new FreeFlightManipulator(RigidPose.Identity);
        var settings = Settings with { DragEnabled = false };
        motion.Update(Frame,0.01f,settings);
        var input = Frame with { PilotStick = Vector2.UnitY, Left = Frame.Left with { Drag = 1 } };
        Assert.True(motion.Update(input,0.1f,settings).Position.Z < 0);
    }

    [Fact]
    public void ExperimentalModeDoesNotRunControllerTurn()
    {
        var motion = new FreeFlightManipulator(RigidPose.Identity);
        motion.Update(Frame,0.01f,Settings);
        var input = Frame with { Left = Frame.Left with { Turn = 1 } };
        motion.Update(input,0.01f,Settings);
        input = input with { Left = input.Left with { Pose = new(Quaternion.CreateFromAxisAngle(Vector3.UnitX,1),Vector3.Zero) } };
        Assert.Equal(RigidPose.Identity,motion.Update(input,0.01f,Settings));
    }
}
