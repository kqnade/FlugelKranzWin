using System.Numerics;
using FlugelKranz.Core;
using FlugelKranz.OpenVR;
using Xunit;

namespace FlugelKranz.Tests;

public class DriverFlightRuntimeTests
{
    [Fact]
    public void PhysicalConversionPreservesPilotAndDashboardInputs()
    {
        var session = new Session();
        session.Frame = session.Frame with { PilotAvailable = true, PilotHeld = true,
            PilotStick = new(0.2f,0.7f), MotionSuspended = true };
        using var runtime = new DriverFlightRuntime(session, () => new Driver());
        var result = runtime.ReadPhysical();
        Assert.True(result.PilotAvailable && result.PilotHeld && result.MotionSuspended);
        Assert.Equal(session.Frame.PilotStick, result.PilotStick);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public void DriverTransformsEveryAxisWithoutChaperoneWrites(float x, float y, float z)
    {
        var session = new Session();
        var driver = new Driver();
        using var runtime = new DriverFlightRuntime(session, () => driver);
        var delta = new RigidPose(Quaternion.CreateFromAxisAngle(new(x,y,z), 0.7f), new(2,3,4));
        runtime.Apply(delta);
        var raw = new RigidPose(Quaternion.Identity, new(1,2,3));
        Assert.True((session.Standing.Inverse() * driver.Transform * raw)
            .NearlyEquals(delta * session.Standing.Inverse() * raw));
        Assert.Equal(0, session.Writes);
    }

    [Fact]
    public void InputUsesPreTransformDriverPoseRatherThanTransformedOpenVrPose()
    {
        var session = new Session();
        var driver = new Driver();
        using var runtime = new DriverFlightRuntime(session, () => driver);
        var before = runtime.ReadPhysical();
        runtime.Apply(new(Quaternion.CreateFromYawPitchRoll(1, 0.7f, 0.3f), new(10,20,30)));
        session.Frame = session.Frame with { Head = new(Quaternion.Identity, new(100,200,300)) };
        Assert.Equal(before.Head, runtime.ReadPhysical().Head);
    }

    [Fact]
    public void RestoreSendsIdentityWithoutChangingChaperone()
    {
        var session = new Session();
        var driver = new Driver();
        using var runtime = new DriverFlightRuntime(session, () => driver);
        runtime.Apply(new(Quaternion.CreateFromYawPitchRoll(1, 0.7f, 0.3f), new(10,20,30)));
        runtime.Restore();
        Assert.True(driver.Transform.NearlyEquals(RigidPose.Identity));
        Assert.Equal(0, session.Writes);
    }

    [Fact]
    public void MissingDriverClosesOpenVrConnection()
    {
        var session = new Session();
        Assert.Throws<IOException>(() => new DriverFlightRuntime(session, () => throw new IOException("missing")));
        Assert.True(session.Disposed);
    }

    [Fact]
    public void ExternalStandingChangeStopsWithoutWritingChaperone()
    {
        var session = new Session();
        using var runtime = new DriverFlightRuntime(session, () => new Driver());
        session.Standing = session.Standing with { Position = new(100,0,0) };
        Assert.Throws<InvalidOperationException>(() => runtime.ReadPhysical());
        Assert.Equal(0, session.Writes);
    }

    [Fact]
    public void StandingOriginIsCheckedAbout10TimesASecondNotEveryTick()
    {
        var session = new Session();
        var time = new ManualTime();
        using var runtime = new DriverFlightRuntime(session, () => new Driver(), time);
        Assert.Equal(1, session.StandingReads);
        runtime.ReadPhysical();
        Assert.Equal(2, session.StandingReads);
        // One second of 3 ms ticks reads the origin 10 times (was 333, each a vrclient log line).
        for (int tick = 0; tick < 333; tick++)
        {
            time.Advance(TimeSpan.FromMilliseconds(3));
            runtime.ReadPhysical();
        }
        Assert.InRange(session.StandingReads - 2, 9, 10);

        // An external change is still caught, within 100 ms.
        session.Standing = session.Standing with { Position = new(100, 0, 0) };
        var caught = TimeSpan.Zero;
        Assert.Throws<InvalidOperationException>(() =>
        {
            for (; caught < TimeSpan.FromSeconds(1); caught += TimeSpan.FromMilliseconds(3))
            {
                time.Advance(TimeSpan.FromMilliseconds(3));
                runtime.ReadPhysical();
            }
        });
        Assert.True(caught <= TimeSpan.FromMilliseconds(100), $"caught after {caught.TotalMilliseconds} ms");
        Assert.Equal(0, session.Writes);
    }

    [Fact]
    public void StaleDriverSampleIsNotTracked()
    {
        var driver = new Driver { Tracked = false };
        using var runtime = new DriverFlightRuntime(new Session(), () => driver);
        Assert.False(runtime.ReadPhysical().HeadTracked);
    }

    internal sealed class ManualTime : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan by) => ticks += by.Ticks;
    }

    private sealed class Driver : IDriverConnection
    {
        public RigidPose Transform = RigidPose.Identity;
        public bool Tracked = true;
        public DriverSample Read(uint device) => new(new(Quaternion.Identity, new(1,2,3)), Tracked);
        public void Refresh() { }
        public void Apply(RigidPose pose) => Transform = pose;
        public void Dispose() => Transform = RigidPose.Identity;
    }
    private sealed class Session : IOpenVrSession
    {
        public RigidPose Standing = new(Quaternion.CreateFromYawPitchRoll(0.4f,0,0), new(3,0,2));
        public InputFrame Frame = new(RigidPose.Identity, true, new(RigidPose.Identity,1,0,0,true), new(RigidPose.Identity,0,1,0,true));
        public int Writes;
        public bool Disposed;
        public int StandingReads;
        public RigidPose ReadWorkingStanding() { StandingReads++; return Standing; }
        public InputFrame ReadRaw() => Frame;
        public (uint Left,uint Right) ControllerDevices() => (1,2);
        public void PreviewStanding(RigidPose pose) { Writes++; }
        public void HidePreview() { Writes++; }
        public void Dispose() => Disposed = true;
    }
}
