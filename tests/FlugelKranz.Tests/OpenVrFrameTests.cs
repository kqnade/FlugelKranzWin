using System.Numerics;
using System.Text.Json;
using FlugelKranz.Core;
using FlugelKranz.OpenVR;
using Valve.VR;
using Xunit;

namespace FlugelKranz.Tests;

public class OpenVrFrameTests
{
    private static readonly InputDigitalActionData_t Held = new() { bActive = true, bState = true };

    [Fact]
    public void HandUsesTheSelectedRawSnapshotPose()
    {
        var pose = new RigidPose(Quaternion.Identity, new(1, 2, 3));
        TrackedDevicePose_t[] raw = [default, new() { bDeviceIsConnected = true,
            bPoseIsValid = true, mDeviceToAbsoluteTracking = OpenVrPose.ToMatrix(pose) }];
        Assert.Equal(pose, OpenVrFrame.Hand(raw, 1, Held, default, default).Pose);
    }

    [Fact]
    public void InactivePressedActionDoesNotStartMovement()
    {
        TrackedDevicePose_t[] raw = [Tracked(RigidPose.Identity)];
        var hand = OpenVrFrame.Hand(raw, 0, new() { bActive = false, bState = true }, default, default);
        Assert.Equal(0, hand.Drag);
    }

    [Fact]
    public void MissingRoleDoesNotUseAnotherDevicesPose()
    {
        Assert.False(OpenVrFrame.Hand([Tracked(RigidPose.Identity)], uint.MaxValue, Held, Held, Held).IsTracked);
    }

    [Fact]
    public void LostTrackingReleasesAllActions()
    {
        var hand = OpenVrFrame.Hand([default], 0, Held, Held, Held);
        Assert.Equal((0f, 0f, 0f), (hand.Drag, hand.Turn, hand.DpadDown));
    }

    [Fact]
    public void StationaryRawHandDoesNotAccumulateInfiniteWalkingDrag()
    {
        var manipulator = new InfiniteWalkingManipulator(RigidPose.Identity);
        TrackedDevicePose_t[] raw = [Tracked(new(Quaternion.Identity, new(0, 1, 0)))];
        InputFrame Frame(InputDigitalActionData_t drag) => new(RigidPose.Identity, true,
            OpenVrFrame.Hand(raw, 0, drag, default, default), new(RigidPose.Identity, 0, 0, 0, false));
        manipulator.Update(Frame(default), 0.01f, InfiniteWalkingSettings.Default);
        manipulator.Update(Frame(Held), 0.01f, InfiniteWalkingSettings.Default);
        raw[0] = Tracked(new(Quaternion.Identity, new(0, 0.9f, 0)));
        for (int i = 0; i < 1000; i++) manipulator.Update(Frame(Held), 0.01f, InfiniteWalkingSettings.Default);
        Assert.InRange(manipulator.Offset.Position.Y, 0.09999f, 0.10001f);
    }

    [Fact]
    public void DefaultTouchBindingsRequireClicksAndExposeOnlyDeclaredActions()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Actions", "actions.json")));
        var actions = manifest.RootElement.GetProperty("actions").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()).ToHashSet();
        using var binding = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Actions", "bindings_touch.json")));
        var sources = binding.RootElement.GetProperty("bindings").GetProperty("/actions/flight").GetProperty("sources");
        Assert.Equal(4, sources.GetArrayLength());
        foreach (var source in sources.EnumerateArray())
        {
            var inputs = source.GetProperty("inputs");
            Assert.False(inputs.TryGetProperty("touch", out _));
            Assert.Contains(inputs.GetProperty("click").GetProperty("output").GetString(), actions);
        }
    }

    private static TrackedDevicePose_t Tracked(RigidPose pose) => new()
    { bDeviceIsConnected = true, bPoseIsValid = true, mDeviceToAbsoluteTracking = OpenVrPose.ToMatrix(pose) };
}
