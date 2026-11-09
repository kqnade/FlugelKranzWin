using System.Numerics;
using FlugelKranz.Core;
using Xunit;

namespace FlugelKranz.Tests;

public class FlightSessionTests
{
    private static readonly RigidPose Tilted = new(
        Quaternion.CreateFromYawPitchRoll(0.4f, 0.7f, -0.3f), new(1, 2, 3));
    private static readonly InputFrame Neutral = new(RigidPose.Identity, true,
        new(RigidPose.Identity, 0, 0, 0, true), new(RigidPose.Identity, 0, 0, 0, true));

    [Fact]
    public void UnchangedPoseDoesNotRequestRuntimeWrite()
    {
        Assert.Null(new Simulation().Tick(Neutral).Offset);
    }

    [Fact]
    public void WalkingTransitionAdvancesWithSuppliedTimeAndCompletesExactly()
    {
        var sim = new Simulation(Tilted);
        sim.Tick(Neutral);
        sim.Mode = FlightMode.InfiniteWalking;
        var halfway = sim.Tick(Neutral, 0.5f);
        var expected = new InfiniteWalkingTransition(Tilted, RigidPose.Identity);
        Assert.Equal(expected.Advance(0.5f), sim.Current);
        Assert.Equal(FlightActivity.ReturningToWalking, halfway.State.Activity);
        Assert.False(halfway.State.Dragging);
        Assert.False(halfway.State.Turning);
        sim.Tick(Neutral, 0.5f);
        Assert.Equal(InfiniteWalkingTransition.CreateTarget(Tilted, RigidPose.Identity), sim.Current);
        Assert.Null(sim.Tick(Neutral).Offset);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("off")]
    [InlineData("dashboard")]
    [InlineData("reset")]
    public void InterruptedDescentKeepsLastAppliedPose(string action)
    {
        var sim = new Simulation(Tilted);
        sim.Tick(Neutral);
        sim.Mode = FlightMode.InfiniteWalking;
        sim.Tick(Neutral, 0.25f);
        var interrupted = sim.Current;
        if (action == "free") sim.Mode = FlightMode.FreeFlight;
        if (action == "off") sim.Enabled = false;
        var frame = Neutral with { MotionSuspended = action == "dashboard" };
        sim.Tick(frame, 0.25f, release: action == "off", reset: action == "reset");
        Assert.Equal(action == "reset" ? RigidPose.Identity : interrupted, sim.Current);
        Assert.Null(sim.Tick(frame, 0.25f).Offset);
    }

    [Fact]
    public void ResetWhileDraggingRequiresNeutralInputBeforeRearming()
    {
        var sim = new Simulation();
        sim.Tick(Neutral);
        sim.Tick(Drag(1, 0));
        sim.Tick(Drag(1, 1));
        Assert.Equal(-1, sim.Current.Position.X);
        sim.Tick(Drag(1, 2), reset: true, release: true);
        Assert.Equal(RigidPose.Identity, sim.Current);
        Assert.Null(sim.Tick(Drag(1, 3)).Offset);
        sim.Tick(Drag(0, 3));
        sim.Tick(Drag(1, 3));
        sim.Tick(Drag(1, 4));
        Assert.Equal(-1, sim.Current.Position.X);
    }

    [Fact]
    public void OffDragSuppressesTurnPilotAndReleaseInertia()
    {
        var sim = new Simulation { Enabled = false };
        sim.Settings = sim.Settings with { FreeFlight = sim.Settings.FreeFlight with { HeadPilotEnabled = true } };
        sim.Tick(Neutral with { PilotAvailable = true });
        sim.Tick(Drag(1, 0));
        var active = Drag(1, 1) with { PilotAvailable = true, PilotStick = Vector2.UnitY };
        active = active with { Right = active.Right with { Turn = 1 } };
        var result = sim.Tick(active);
        Assert.Equal(new RigidPose(Quaternion.Identity, new(-1, 0, 0)), sim.Current);
        Assert.True(result.State.Dragging);
        Assert.False(result.State.Turning);
        Assert.Equal(FlightActivity.OffDrag, result.State.Activity);
        sim.Tick(Neutral);
        Assert.Null(sim.Tick(Neutral, 1).Offset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArmedPilotThrustIsAppliedOnlyWhileEnabled(bool enabled)
    {
        var sim = new Simulation { Enabled = enabled };
        sim.Settings = sim.Settings with { FreeFlight = sim.Settings.FreeFlight with { HeadPilotEnabled = true } };
        var neutral = Neutral with { PilotAvailable = true };
        sim.Tick(neutral);
        sim.Tick(neutral with { PilotStick = Vector2.UnitY }, 0.1f);
        if (enabled) Assert.True(sim.Current.Position.Z < -0.001f);
        else Assert.Equal(RigidPose.Identity, sim.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeightReturnsToConnectionBaselineInsteadOfWorldZero(bool partialReset)
    {
        var original = new RigidPose(Quaternion.Identity, new(0, 0.7f, 0));
        var sim = new Simulation(Tilted, original);
        if (partialReset) sim.Mode = FlightMode.InfiniteWalking;
        sim.Tick(Neutral);
        sim.Mode = FlightMode.InfiniteWalking;
        var frame = partialReset ? Neutral with { Left = Neutral.Left with { DpadDown = 1 } } : Neutral;
        sim.Tick(frame, 1);
        Assert.Equal(original.Position.Y, sim.Current.Position.Y, 5);
    }

    [Fact]
    public void InputOverrideSurvivesStaleSettingsUntilAcknowledged()
    {
        var sim = new Simulation();
        var both = Neutral with
        {
            Left = Neutral.Left with { DpadDown = 1 }, Right = Neutral.Right with { DpadDown = 1 }
        };
        sim.Tick(Neutral);
        sim.Tick(both, 1);
        Assert.Equal(FlightMode.InfiniteWalking, sim.Session.SelectedMode);
        Assert.Equal(FlightMode.InfiniteWalking, sim.Session.InputMode(FlightMode.FreeFlight));
        sim.Tick(Neutral);
        Assert.Equal(FlightMode.InfiniteWalking, sim.Session.SelectedMode);
        sim.Mode = FlightMode.InfiniteWalking;
        sim.Tick(Neutral);
        sim.Mode = FlightMode.FreeFlight;
        sim.Tick(Neutral);
        Assert.Equal(FlightMode.FreeFlight, sim.Session.SelectedMode);
    }

    [Fact]
    public void WalkingEntryTakesPriorityOverPartialReset()
    {
        var sim = new Simulation(Tilted);
        sim.Tick(Neutral);
        var one = Neutral with { Left = Neutral.Left with { DpadDown = 1 } };
        sim.Tick(one, 0.75f);
        sim.Mode = FlightMode.InfiniteWalking;
        var result = sim.Tick(one, 0.25f);
        Assert.False(result.SpaceResetStarted);
        Assert.Equal(FlightActivity.ReturningToWalking, result.State.Activity);
    }

    [Fact]
    public void FullResetPrecedesPartialResetOnSameTick()
    {
        var sim = new Simulation(Tilted);
        sim.Mode = FlightMode.InfiniteWalking;
        var one = Neutral with { Left = Neutral.Left with { DpadDown = 1 } };
        sim.Tick(one, 0.75f);
        var result = sim.Tick(one, 0.25f, release: true, reset: true);
        Assert.True(result.SpaceResetStarted);
        Assert.Equal(RigidPose.Identity, sim.Current);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("off")]
    [InlineData("dashboard")]
    public void PartialResetCanBeInterrupted(string action)
    {
        var sim = new Simulation(Tilted);
        var one = Neutral with { Left = Neutral.Left with { DpadDown = 1 } };
        sim.Tick(one, 0.75f);
        Assert.True(sim.Tick(one, 0.25f).SpaceResetStarted);
        var interrupted = sim.Current;
        if (action == "mode") sim.Mode = FlightMode.InfiniteWalking;
        if (action == "off") sim.Enabled = false;
        var frame = one with { MotionSuspended = action == "dashboard" };
        var result = sim.Tick(frame, 0, release: action == "off");
        Assert.Equal(interrupted, sim.Current);
        Assert.NotEqual(FlightActivity.ResettingSpace, result.State.Activity);
        Assert.False(result.SpaceResetStarted);
    }

    [Theory]
    [InlineData(TurnOrigin.Head, 0)]
    [InlineData(TurnOrigin.TrackedElementsMidpoint, 0)]
    [InlineData(TurnOrigin.Head, 1)]
    [InlineData(TurnOrigin.Head, 2)]
    [InlineData(TurnOrigin.Head, 3)]
    public void WalkingEntryCapturesPivotBeforeReleasingDrag(TurnOrigin origin, int hands)
    {
        var sim = new Simulation(Tilted);
        sim.Settings = sim.Settings with { FreeFlight = sim.Settings.FreeFlight with { TurnOrigin = origin } };
        var frame = Neutral with
        {
            Head = new(Quaternion.Identity, new(0, 1.7f, 0)),
            Left = Neutral.Left with { Pose = new(Quaternion.Identity, new(-0.3f, 1.2f, 0.1f)) },
            Right = Neutral.Right with { Pose = new(Quaternion.Identity, new(0.6f, 1.1f, 0.4f)) }
        };
        sim.Tick(frame);
        frame = frame with
        {
            Left = frame.Left with { Drag = (hands & 1) != 0 ? 1 : 0 },
            Right = frame.Right with { Drag = (hands & 2) != 0 ? 1 : 0 }
        };
        sim.Tick(frame);
        Vector3 pivot = hands switch
        {
            1 => frame.Left.Pose.Position,
            2 => frame.Right.Pose.Position,
            3 => (frame.Left.Pose.Position + frame.Right.Pose.Position) / 2,
            _ => origin == TurnOrigin.Head ? frame.Head.Position
                : (frame.Head.Position + frame.Left.Pose.Position + frame.Right.Pose.Position) / 3
        };
        var expected = new InfiniteWalkingTransition(sim.Current, RigidPose.Identity, pivot);
        sim.Mode = FlightMode.InfiniteWalking;
        sim.Tick(frame, 0.25f);
        Assert.Equal(expected.Advance(0.25f), sim.Current);
    }

    [Theory]
    [InlineData(false, false, true, true, FlightActivity.OffDrag)]
    [InlineData(false, true, true, true, FlightActivity.OffSuspended)]
    [InlineData(true, true, false, false, FlightActivity.Suspended)]
    [InlineData(true, false, false, false, FlightActivity.WaitingForHead)]
    [InlineData(true, false, true, false, FlightActivity.WaitingForControllers)]
    [InlineData(true, false, true, true, FlightActivity.Ready)]
    public void SnapshotPreservesStatusPriority(bool enabled, bool suspended, bool head, bool hands,
        object expected)
    {
        var sim = new Simulation { Enabled = enabled };
        var frame = Neutral with { MotionSuspended = suspended, HeadTracked = head,
            Left = Neutral.Left with { IsTracked = hands } };
        Assert.Equal(expected, sim.Tick(frame).State.Activity);
    }

    [Theory]
    [InlineData(0.1f, FlightMode.FreeFlight, true)]
    [InlineData(0.5f, FlightMode.FreeFlight, true)]
    [InlineData(0.9f, FlightMode.FreeFlight, true)]
    [InlineData(0.5f, FlightMode.FreeFlight, false)]
    [InlineData(0.5f, FlightMode.InfiniteWalking, true)]
    public void OffCancelsPartialResetAtAppliedPoseAndReenableDoesNotResumeIt(
        float progress, FlightMode mode, bool dragEnabled)
    {
        var sim = new Simulation(Tilted);
        sim.Mode = mode;
        sim.Settings = sim.Settings with { FreeFlight = sim.Settings.FreeFlight with { DragEnabled = dragEnabled } };
        sim.Tick(Neutral);
        // Start a partial reset directly so its progression is independent of the hold timer.
        var result = sim.Session.Step(Neutral, progress, sim.Settings.Normalized(), true,
            sim.Current, sim.Original, spaceResetRequested: true);
        Assert.True(result.SpaceResetStarted);
        var interrupted = Assert.IsType<RigidPose>(result.Offset);
        Assert.NotEqual(Tilted, interrupted);
        sim.Current = interrupted;
        sim.Enabled = false;
        Assert.Null(sim.Tick(Neutral, release: true).Offset);
        Assert.Null(sim.Tick(Neutral, 1).Offset);
        sim.Enabled = true;
        Assert.Null(sim.Tick(Neutral, release: true).Offset);
        Assert.Null(sim.Tick(Neutral, 1).Offset);
        Assert.Equal(interrupted, sim.Current);
    }

    private static InputFrame Drag(float grip, float x) => Neutral with
    {
        Left = Neutral.Left with { Drag = grip, Pose = new(Quaternion.Identity, new(x, 0, 0)) }
    };

    // Models successful application only; actual I/O failure behavior is covered by FlightControllerTests.
    private sealed class Simulation
    {
        public FlightSession Session { get; }
        public RigidPose Current { get; set; }
        public RigidPose Original { get; }
        public bool Enabled { get; set; } = true;
        public FlugelKranzSettings Settings { get; set; } = FlugelKranzSettings.Default with
        {
            Mode = FlightMode.FreeFlight,
            FreeFlight = FlightMotionSettings.Default with { HeadPilotEnabled = false, DragSmoothSeconds = 0 }
        };
        public FlightMode Mode { set => Settings = Settings with { Mode = value }; }
        public Simulation(RigidPose? start = null, RigidPose? original = null)
        {
            Current = start ?? RigidPose.Identity;
            Original = original ?? RigidPose.Identity;
            Session = new(Current);
        }
        public FlightStepResult Tick(InputFrame frame, float dt = 0.01f, bool release = false, bool reset = false)
        {
            var settings = Settings.Normalized();
            var gesture = Session.PrepareInput(frame, dt, settings.Mode);
            if (release) Session.ReleaseInputs(Current);
            if (reset)
            {
                Current = Original;
                Session.ResetTo(Current);
            }
            var result = Session.Step(frame, dt, settings, Enabled, Current, Original,
                gesture == FlightGesture.ResetSpace);
            if (result.Offset is { } offset) Current = offset;
            return result;
        }
    }
}
