using System.Collections.Concurrent;
using System.Numerics;
using FlugelKranz.Core;
using Xunit;

namespace FlugelKranz.Tests;

public class FlightControllerTests
{
    [Fact]
    public async Task FlightOffAllowsDragButDoesNotStartReleaseInertiaOrThrust()
    {
        var runtime = new FakeRuntime();
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress, FreeFlightSettings);
        controller.RefreshDragConnection();
        await WaitForFrame(runtime, Frame(0,0));
        await WaitForFrame(runtime, Frame(1,0));
        await WaitForFrame(runtime, Frame(1,1));
        await Wait(() => runtime.CurrentOffset.Position.X < -0.9f);
        await WaitForFrame(runtime, Frame(0,1));
        var stopped = runtime.CurrentOffset;
        await WaitForFrame(runtime, Frame(0,2) with { PilotAvailable = true, PilotStick = Vector2.UnitY });
        await WaitForFrame(runtime, Frame(0,3) with { PilotAvailable = true, PilotStick = Vector2.UnitY });
        Assert.Equal(stopped,runtime.CurrentOffset);
        Assert.DoesNotContain(progress.Statuses, s => s.Enabled);
        Assert.False(runtime.PilotEnabled);
    }

    [Fact]
    public async Task DragDisabledAndFlightOffKeepOffsetFixed()
    {
        var runtime = new FakeRuntime();
        var progress = new Recorder();
        var settings = FreeFlightSettings() with { FreeFlight = FlightMotionSettings.Default with { DragEnabled = false } };
        await using var controller = new FlightController(() => runtime, progress, () => settings);
        controller.OpenBindings();
        await WaitForFrame(runtime,Frame(0,0));
        await WaitForFrame(runtime,Frame(1,0));
        await WaitForFrame(runtime,Frame(1,2));
        Assert.Equal(RigidPose.Identity,runtime.CurrentOffset);
    }

    [Fact]
    public async Task StatusIncludesRuntimeInputDiagnostics()
    {
        var runtime = new FakeRuntime();
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress);
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.InputDiagnostics == "test input snapshot"));
    }

    [Fact]
    public async Task DashboardStopsInertiaAndClosingDoesNotRestoreIt()
    {
        var runtime = new FakeRuntime();
        var progress = new Recorder();
        var settings = FlugelKranzSettings.Default with
        {
            Mode = FlightMode.FreeFlight,
            FreeFlight = FlightMotionSettings.Default with
            { HeadPilotEnabled = true, InertiaDecelerationPerSecond = 0, DragSmoothSeconds = 0 }
        };
        await using var controller = new FlightController(() => runtime, progress, () => settings);
        controller.SetEnabled(true);
        var neutral = Frame(0,0) with { PilotAvailable = true };
        await WaitForFrame(runtime, neutral);
        await WaitForFrame(runtime, neutral with { PilotStick = Vector2.UnitY });
        await Wait(() => runtime.CurrentOffset.Position.Z < -0.005f);
        await WaitForFrame(runtime, neutral with { MotionSuspended = true });
        var stopped = runtime.CurrentOffset;
        await WaitForFrame(runtime, neutral with { MotionSuspended = true });
        Assert.Equal(stopped, runtime.CurrentOffset);
        await WaitForFrame(runtime, neutral);
        await WaitForFrame(runtime, neutral);
        Assert.Equal(stopped, runtime.CurrentOffset);
        Assert.True(runtime.PilotEnabled);
        controller.SetEnabled(false);
        await Wait(() => !runtime.PilotEnabled);
    }

    [Fact]
    public async Task BindingEditorCanOpenWhileMovementStaysOff()
    {
        var runtime = new FakeRuntime();
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress);
        controller.OpenBindings();
        await Wait(() => runtime.BindingsOpened > 0);
        Assert.DoesNotContain(progress.Statuses, s => s.Enabled);
        Assert.Equal(RigidPose.Identity, runtime.CurrentOffset);
    }

    [Fact]
    public async Task DisposeFailureReportsOffAndAllowsControllerShutdown()
    {
        var runtime = new FakeRuntime { ThrowOnDispose = true };
        var progress = new Recorder();
        var controller = new FlightController(() => runtime, progress);
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Connected));
        await controller.DisposeAsync();
        Assert.Contains(progress.Statuses, s => !s.Enabled && !s.Connected && s.Message.Contains("dispose failed"));
    }

    [Fact]
    public async Task OffRetainsOffsetResetRestoresAndShutdownDisposes()
    {
        var runtime = new FakeRuntime();
        var progress = new Recorder();
        var controller = new FlightController(() => runtime, progress, FreeFlightSettings);
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Connected));
        runtime.Frame = Frame(1, 0);
        await Wait(() => progress.Statuses.Any(s => s.Dragging));
        runtime.Frame = Frame(1, 1);
        await Wait(() => runtime.CurrentOffset.Position.X < -0.9f);
        controller.SetEnabled(false);
        var retained = runtime.CurrentOffset;
        runtime.Frame = Frame(1, 2);
        await Wait(() => progress.Statuses.Any(s => s.Connected && !s.Enabled));
        Assert.Equal(retained, runtime.CurrentOffset);
        controller.Reset();
        await Wait(() => runtime.CurrentOffset == RigidPose.Identity);
        await controller.DisposeAsync();
        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task ResetWhileEnabledRestoresOffsetAndRequiresGripRearm()
    {
        var runtime = new FakeRuntime();
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress, FreeFlightSettings);
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Connected));

        runtime.Frame = Frame(1, 0);
        await Wait(() => progress.Statuses.Any(s => s.Dragging));
        runtime.Frame = Frame(1, 1);
        await Wait(() => runtime.CurrentOffset.Position.X < -0.9f);

        controller.Reset();
        await Wait(() => runtime.Restores > 0 && runtime.CurrentOffset == RigidPose.Identity);

        // The reset keeps the controller enabled but clears the active grip.
        await WaitForFrame(runtime, Frame(1, 2));
        Assert.Equal(RigidPose.Identity, runtime.CurrentOffset);

        // Releasing and gripping again arms Drag for the next movement.
        await WaitForFrame(runtime, Frame(0, 2));
        await WaitForFrame(runtime, Frame(1, 2));
        await WaitForFrame(runtime, Frame(1, 3));
        await Wait(() => runtime.CurrentOffset.Position.X < -0.9f);
        Assert.Contains(progress.Statuses, s => s.Connected && s.Enabled);
    }

    [Fact]
    public async Task ConnectionFailureReportsOffWithoutThrowingOnUiThread()
    {
        var progress = new Recorder();
        await using var controller = new FlightController(() => throw new InvalidOperationException("no runtime"), progress);
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Message == "no runtime"));
        Assert.False(progress.Statuses.Last().Enabled);
    }

    [Fact]
    public async Task ReadFailureRestoresOwnedOffsetAndDisposes()
    {
        var runtime = new FakeRuntime();
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress);
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Connected));
        runtime.ThrowOnRead = true;
        await Wait(() => runtime.Disposed);
        Assert.True(runtime.Restores > 0);
        await Wait(() => progress.Statuses.Any(s => s.Message.Contains("lost")));
    }

    [Fact]
    public async Task BothModeButtonsHeldForOneSecondToggleOnceUntilReleased()
    {
        var runtime = new FakeRuntime();
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress,
            () => FlugelKranzSettings.Default with { Mode = FlightMode.InfiniteWalking });
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Connected));

        runtime.Frame = ModeFrame(1);
        await Wait(() => progress.Statuses.Any(
            s => s.Mode == FlightMode.FreeFlight && s.Message.Contains("切り替え")));
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(
            progress.Statuses,
            s => s.Mode == FlightMode.InfiniteWalking && s.Message.Contains("切り替え"));

        await WaitForFrame(runtime, ModeFrame(0));
        runtime.Frame = ModeFrame(1);
        await Wait(() => progress.Statuses.Any(
            s => s.Mode == FlightMode.InfiniteWalking && s.Message.Contains("切り替え")));
    }

    [Fact]
    public async Task OneDpadDownHeldForOneSecondLevelsFreeFlightWithoutChangingMode()
    {
        var runtime = new FakeRuntime();
        var current = new RigidPose(
            Quaternion.CreateFromYawPitchRoll(0.4f, 0.7f, -0.3f),
            new(1, 2, 3));
        runtime.Apply(current);
        var progress = new Recorder();
        await using var controller = new FlightController(
            () => runtime,
            progress,
            FreeFlightSettings);
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Connected));
        var held = new InputFrame(
            new(Quaternion.Identity, new(0, 1.7f, 0)),
            true,
            new(RigidPose.Identity, 0, 0, 1, true),
            new(new(Quaternion.Identity, new(0.4f, 1.2f, -0.3f)), 1, 0, 0, true));
        Vector3 turnPivot = held.Right.Pose.Position;
        var expectedTransition = SpaceResetTransition.CreateFreeFlight(
            current,
            held.Head,
            turnPivot);
        var expected = expectedTransition.Advance(1);

        runtime.Frame = held;
        await Wait(() => progress.Statuses.Any(s => s.Message.Contains("水平へ戻しています")));
        await Wait(() => runtime.CurrentOffset.NearlyEquals(expected, 0.001f));

        Assert.DoesNotContain(progress.Statuses, s => s.Message.Contains("切り替え"));
        Assert.All(progress.Statuses, s => Assert.Equal(FlightMode.FreeFlight, s.Mode));
        Assert.True(Vector3.Distance(
            current.Transform(turnPivot),
            runtime.CurrentOffset.Transform(turnPivot)) < 0.001f);
    }

    [Fact]
    public async Task OneDpadDownHeldForOneSecondResetsOnlyInfiniteWalkingHeight()
    {
        var runtime = new FakeRuntime();
        var current = new RigidPose(
            Quaternion.CreateFromYawPitchRoll(0.4f, 0.2f, -0.3f),
            new(1, 2, 3));
        runtime.Apply(current);
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress,
            () => FlugelKranzSettings.Default with { Mode = FlightMode.InfiniteWalking });
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Connected));

        runtime.Frame = DpadFrame(0, 1);
        await Wait(() => progress.Statuses.Any(s => s.Message.Contains("高さを戻しています")));
        await Wait(() => MathF.Abs(runtime.CurrentOffset.Position.Y) < 0.001f);

        Assert.True(1 - MathF.Abs(Quaternion.Dot(
            current.Orientation,
            runtime.CurrentOffset.Orientation)) < 0.0001f);
        Assert.Equal(current.Position.X, runtime.CurrentOffset.Position.X);
        Assert.Equal(current.Position.Z, runtime.CurrentOffset.Position.Z);
    }

    [Fact]
    public async Task FreeFlightToInfiniteWalkingLevelsOverOneSecond()
    {
        var runtime = new FakeRuntime();
        var current = new RigidPose(
            Quaternion.CreateFromYawPitchRoll(0.4f, 0.7f, -0.3f),
            new(1, 2, 3));
        runtime.Apply(current);
        var settings = FlugelKranzSettings.Default with { Mode = FlightMode.FreeFlight };
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress, () => settings);
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Connected));

        settings = settings with { Mode = FlightMode.InfiniteWalking };
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.InRange(runtime.CurrentOffset.Position.Y, 0.01f, 1.99f);

        var target = InfiniteWalkingTransition.CreateTarget(current, RigidPose.Identity);
        await Wait(() => runtime.CurrentOffset.NearlyEquals(target, 0.001f));
        Assert.Contains(progress.Statuses, s => s.Message.Contains("戻しています"));
    }

    [Fact]
    public async Task ReportsActiveValveIndexForceForBothHands()
    {
        var runtime = new FakeRuntime();
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress);
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Connected));

        runtime.Frame = Frame(0, 0) with
        {
            Left = new(RigidPose.Identity, 0, 0, 0, true)
            {
                TrackpadForceActive = true,
                TrackpadForce = 0.42f
            },
            Right = new(RigidPose.Identity, 0, 0, 0, true)
            {
                TrackpadForceActive = true,
                TrackpadForce = 0.87f
            }
        };

        await Wait(() => progress.Statuses.Any(s =>
            s.LeftTrackpadForce == 0.42f && s.RightTrackpadForce == 0.87f));
    }

    [Theory]
    [InlineData(TurnOrigin.Head, 0)]
    [InlineData(TurnOrigin.TrackedElementsMidpoint, 0)]
    [InlineData(TurnOrigin.Head, 1)]
    [InlineData(TurnOrigin.Head, 2)]
    [InlineData(TurnOrigin.Head, 3)]
    public async Task WalkingEntryCapturesConfiguredOrActiveDragPivot(TurnOrigin origin, int hands)
    {
        var current = new RigidPose(Quaternion.CreateFromYawPitchRoll(0.4f, 2.9f, -0.3f), new(1, 5, 3));
        var frame = new InputFrame(new(Quaternion.Identity, new(0.4f, 1.7f, -0.2f)), true,
            new(new(Quaternion.Identity, new(-0.3f, 1.2f, 0.1f)), 0, 0, 0, true),
            new(new(Quaternion.Identity, new(0.6f, 1.1f, 0.4f)), 0, 0, 0, true));
        var runtime = new FakeRuntime { Frame = frame };
        runtime.Apply(current);
        var settings = FreeFlightSettings() with
        {
            FreeFlight = FlightMotionSettings.Default with { HeadPilotEnabled = false, TurnOrigin = origin }
        };
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress, () => settings);
        controller.SetEnabled(true);
        await WaitForFrame(runtime, frame);
        frame = frame with
        {
            Left = frame.Left with { Drag = (hands & 1) != 0 ? 1 : 0 },
            Right = frame.Right with { Drag = (hands & 2) != 0 ? 1 : 0 }
        };
        await WaitForFrame(runtime, frame);
        if (hands != 0) await Wait(() => progress.Statuses.Any(s => s.Dragging));
        Vector3 pivot = hands switch
        {
            1 => frame.Left.Pose.Position,
            2 => frame.Right.Pose.Position,
            3 => (frame.Left.Pose.Position + frame.Right.Pose.Position) / 2,
            _ => origin == TurnOrigin.Head ? frame.Head.Position
                : (frame.Head.Position + frame.Left.Pose.Position + frame.Right.Pose.Position) / 3
        };
        Vector3 start = runtime.CurrentOffset.Transform(pivot);
        settings = settings with { Mode = FlightMode.InfiniteWalking };
        await Wait(() => progress.Statuses.Any(s => s.Message.Contains("戻しています")));
        Assert.Equal(start.X, runtime.CurrentOffset.Transform(pivot).X, 4);
        Assert.Equal(start.Z, runtime.CurrentOffset.Transform(pivot).Z, 4);
        // Tracking movement after entry must not change the captured physical pivot.
        runtime.Frame = frame with { Head = frame.Head with { Position = new(2, 3, 4) } };
        await Wait(() => MathF.Abs(runtime.CurrentOffset.Transform(pivot).Y - pivot.Y) < 0.0001f);
        Assert.Equal(start.X, runtime.CurrentOffset.Transform(pivot).X, 4);
        Assert.Equal(start.Z, runtime.CurrentOffset.Transform(pivot).Z, 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyFailureStopsWorkerAndAttemptsRestoreBeforeDispose(bool restoreFails)
    {
        var runtime = new FakeRuntime();
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress, FreeFlightSettings);
        controller.SetEnabled(true);
        await WaitForFrame(runtime, Frame(0, 0));
        await WaitForFrame(runtime, Frame(1, 0));
        runtime.ThrowOnApply = true;
        runtime.ThrowOnRestore = restoreFails;
        runtime.ThrowOnDispose = restoreFails;
        runtime.Frame = Frame(1, 1);
        await Wait(() => progress.Statuses.Any(s => !s.Connected && s.Message.Contains("apply failed")));

        Assert.Equal(new[] { "apply", "restore", "dispose" }, runtime.FailuresAndCleanup.ToArray());
        var final = progress.Statuses.Last();
        Assert.False(final.Enabled);
        if (restoreFails)
        {
            Assert.Contains("restore failed", final.Message);
            Assert.Contains("dispose failed", final.Message);
        }
        else Assert.Equal(runtime.OriginalOffset, runtime.CurrentOffset);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("reset")]
    [InlineData("free")]
    [InlineData("dashboard")]
    public async Task WalkingTransitionCanBeInterruptedWithoutContinuingDescent(string action)
    {
        var runtime = new FakeRuntime();
        runtime.Apply(new(Quaternion.CreateFromYawPitchRoll(0.4f, 0.7f, -0.3f), new(1, 2, 3)));
        var settings = FreeFlightSettings();
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress, () => settings);
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Connected));
        settings = settings with { Mode = FlightMode.InfiniteWalking };
        await Wait(() => progress.Statuses.Any(s => s.Message == "無限歩行モードへ戻しています…"));
        RigidPose interrupted = default;
        runtime.BeforeNextRead = () =>
        {
            interrupted = runtime.CurrentOffset;
            switch (action)
            {
                case "off": controller.SetEnabled(false); break;
                case "reset": controller.Reset(); break;
                case "free": settings = settings with { Mode = FlightMode.FreeFlight }; break;
                case "dashboard": runtime.Frame = Frame(0, 0) with { MotionSuspended = true }; break;
            }
        };
        int reads = runtime.Reads;
        await Wait(() => runtime.Reads > reads + 2);
        var stopped = runtime.CurrentOffset;
        await WaitForFrame(runtime, runtime.Frame);
        Assert.Equal(stopped, runtime.CurrentOffset);
        if (action == "reset") Assert.Equal(RigidPose.Identity, stopped);
        else Assert.Equal(interrupted, stopped);
    }

    [Fact]
    public async Task OffDuringPartialResetPreservesLegacyDragBaseline()
    {
        var baseline = new RigidPose(Quaternion.CreateFromYawPitchRoll(0.4f, 0.7f, -0.3f), new(1, 2, 3));
        var runtime = new FakeRuntime();
        runtime.Apply(baseline);
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress, FreeFlightSettings);
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Connected));
        runtime.Frame = DpadFrame(1, 0);
        await Wait(() => progress.Statuses.Any(s => s.Message.Contains("水平へ戻しています")));
        runtime.BeforeNextRead = () => controller.SetEnabled(false);
        int reads = runtime.Reads;
        await Wait(() => runtime.Reads > reads + 2);
        Assert.Equal(baseline, runtime.CurrentOffset);
    }

    [Fact]
    public async Task WalkingEntryUsesRuntimeConnectionBaseline()
    {
        var runtime = new FakeRuntime { OriginalOffset = new(Quaternion.Identity, new(0, 0.7f, 0)) };
        runtime.Apply(new(Quaternion.Identity, new(1, 2, 3)));
        var settings = FreeFlightSettings();
        var progress = new Recorder();
        await using var controller = new FlightController(() => runtime, progress, () => settings);
        controller.SetEnabled(true);
        await Wait(() => progress.Statuses.Any(s => s.Connected));
        settings = settings with { Mode = FlightMode.InfiniteWalking };
        var target = new Vector3(1, 0.7f, 3);
        await Wait(() => runtime.CurrentOffset.Position == target);
        Assert.Equal(target, runtime.CurrentOffset.Position);
    }

    private static InputFrame Frame(float grip, float x) => new(RigidPose.Identity, true,
        new(new(Quaternion.Identity, new(x, 0, 0)), grip, 0, 0, true),
        new(RigidPose.Identity, 0, 0, 0, true));
    private static FlugelKranzSettings FreeFlightSettings() =>
        FlugelKranzSettings.Default with { Mode = FlightMode.FreeFlight };
    private static InputFrame ModeFrame(float value) => new(
        RigidPose.Identity,
        true,
        new(RigidPose.Identity, 0, 0, value, true),
        new(RigidPose.Identity, 0, 0, value, true));
    private static InputFrame DpadFrame(float left, float right) => new(
        RigidPose.Identity,
        true,
        new(RigidPose.Identity, 0, 0, left, true),
        new(RigidPose.Identity, 0, 0, right, true));
    private static async Task Wait(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static async Task WaitForFrame(FakeRuntime runtime, InputFrame frame)
    {
        int reads = runtime.Reads;
        runtime.Frame = frame;
        await Wait(() => runtime.Reads > reads + 2);
    }
    private sealed class Recorder : IProgress<FlightStatus>
    {
        public ConcurrentQueue<FlightStatus> Statuses { get; } = new();
        public void Report(FlightStatus value) => Statuses.Enqueue(value);
    }
    private sealed class FakeRuntime : IFlightRuntime, IFlightBindings, IFlightInputControl, IFlightInputDiagnostics
    {
        private readonly object gate = new();
        private InputFrame frame = FlightControllerTests.Frame(0, 0);
        private RigidPose offset = RigidPose.Identity;
        public InputFrame Frame { get { lock (gate) return frame; } set { lock (gate) frame = value; } }
        public int Reads => Volatile.Read(ref reads);
        public RigidPose OriginalOffset { get; init; } = RigidPose.Identity;
        public RigidPose CurrentOffset { get { lock (gate) return offset; } }
        public volatile bool Disposed, ThrowOnRead, ThrowOnDispose, ThrowOnApply, ThrowOnRestore;
        public ConcurrentQueue<string> FailuresAndCleanup { get; } = new();
        public Action? BeforeNextRead;
        public int Restores;
        public int BindingsOpened;
        public string InputDiagnostics => "test input snapshot";
        public volatile bool PilotEnabled;
        public void SetPilotInputEnabled(bool value) => PilotEnabled = value;
        public void OpenBindings() => Interlocked.Increment(ref BindingsOpened);
        private int reads;
        public InputFrame ReadPhysical()
        {
            Interlocked.Exchange(ref BeforeNextRead, null)?.Invoke();
            Interlocked.Increment(ref reads);
            return ThrowOnRead ? throw new IOException("lost") : Frame;
        }
        public void Apply(RigidPose value)
        {
            if (ThrowOnApply)
            {
                FailuresAndCleanup.Enqueue("apply");
                throw new IOException("apply failed");
            }
            lock (gate) offset = value;
        }
        public void Restore()
        {
            FailuresAndCleanup.Enqueue("restore");
            Interlocked.Increment(ref Restores);
            if (ThrowOnRestore) throw new IOException("restore failed");
            lock (gate) offset = OriginalOffset;
        }
        public void Dispose()
        {
            FailuresAndCleanup.Enqueue("dispose");
            Disposed = true;
            if (ThrowOnDispose) throw new IOException("dispose failed");
        }
    }
}
