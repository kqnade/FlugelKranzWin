using System.Numerics;
using System.Text.Json;
using FlugelKranz.Core;
using Xunit;

namespace FlugelKranz.Tests;

public class FreeFlightReplayTests
{
    // Captured before phase 3 at a550534. These fixed input sequences exercise smoothing,
    // mixed/two-hand grips, release inertia, lost tracking, settings changes and pilot input.
    public sealed record Snapshot(int Scenario, int Tick, float[] Pose, bool[] State);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FixedInputSequenceMatchesPreRefactorTrajectory(int scenario)
    {
        using var resource = typeof(FreeFlightReplayTests).Assembly.GetManifestResourceStream(
            "FlugelKranz.Tests.FreeFlightReplayBaseline.json")!;
        var expected = JsonSerializer.Deserialize<Snapshot[]>(resource)!
            .Where(s => s.Scenario == scenario).ToArray();
        var actual = Replay(scenario).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.Equal(expected[i].Tick, actual[i].Tick);
            Assert.Equal(expected[i].State, actual[i].State);
            for (int component = 0; component < 7; component++)
                Assert.True(MathF.Abs(expected[i].Pose[component] - actual[i].Pose[component]) <= 0.000001f,
                    $"Scenario {scenario}, tick {i}, component {component}: expected {expected[i].Pose[component]}, actual {actual[i].Pose[component]}");
        }
    }

    private static IEnumerable<Snapshot> Replay(int scenario)
    {
        var engine = new FreeFlightManipulator(new(
            Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0.1f), new(1, 2, 3)));
        for (int tick = 0; tick < 96; tick++)
        {
            int phase = tick / 8;
            float t = tick * 0.01f;
            var frame = new InputFrame(
                new(Quaternion.CreateFromYawPitchRoll(t * 0.3f, t * 0.2f, 0), new(0, 1.7f, 0)),
                tick != 42,
                new(new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, t), new(-0.3f + t, 1.2f, -0.4f)),
                    phase is 1 or 2 or 5 ? 1 : 0, phase is 4 or 5 ? 1 : 0, 0, tick != 43),
                new(new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, t * 0.8f), new(0.3f, 1.2f + t, -0.4f)),
                    phase == 2 ? 1 : 0, phase is 1 or 4 or 5 ? 1 : 0, 0, true))
            {
                PilotAvailable = true,
                PilotHeld = tick is 67 or 68,
                PilotStick = phase == 9 ? Vector2.UnitY : Vector2.Zero
            };
            var settings = FlightMotionSettings.Default with
            {
                HeadPilotEnabled = scenario == 2 && tick >= 64,
                DragEnabled = tick != 47,
                DragSmoothSeconds = scenario == 0 ? 0 : 0.08f,
                TurnSmoothSeconds = scenario == 0 ? 0 : 0.1f,
                TurnOrigin = scenario == 1 ? TurnOrigin.TrackedElementsMidpoint : TurnOrigin.Head,
                InertiaCutoffEnabled = false
            };
            if (tick == 80) engine.Release();
            if (tick == 88) engine.SetOffset(RigidPose.Identity);
            var pose = engine.Update(frame, tick % 3 == 0 ? 0.02f : 0.01f, settings);
            yield return new(scenario, tick,
                [pose.Position.X, pose.Position.Y, pose.Position.Z,
                 pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W],
                [engine.IsDragging, engine.IsTurning, engine.IsHeadPiloting,
                 engine.HasLinearInertia, engine.HasAngularInertia]);
        }
    }
}
