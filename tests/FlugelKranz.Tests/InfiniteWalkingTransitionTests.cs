using System.Numerics;
using FlugelKranz.Core;
using Xunit;

namespace FlugelKranz.Tests;

public class InfiniteWalkingTransitionTests
{
    // Adapted from ReinaS-64892/FlugelKranz 1b6da75.
    [Theory]
    [InlineData(0.5f, 0.3f)]
    [InlineData(1.6f, -0.8f)]
    [InlineData(2.8f, 1.4f)]
    [InlineData(3.1415927f, 0f)]
    [InlineData(0f, 3.1415927f)]
    public void CapturedPivotFollowsVerticalLineThroughoutDescent(float pitch, float roll)
    {
        var original = new RigidPose(Quaternion.CreateFromYawPitchRoll(0.2f, -0.1f, 0.05f), new(2, 0.3f, -1));
        var current = new RigidPose(
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.7f, pitch, roll) * original.Orientation), new(6, 5, -4));
        var pivot = new Vector3(0.8f, 1.6f, -0.4f);
        Vector3 start = current.Transform(pivot);
        float landingY = original.Transform(pivot).Y;
        var transition = new InfiniteWalkingTransition(current, original, pivot);
        for (int step = 1; step <= 10; step++)
        {
            Vector3 point = transition.Advance(0.1f).Transform(pivot);
            float u = step / 10f;
            float eased = u * u * (3 - 2 * u);
            Assert.True(Vector3.Distance(new(start.X, start.Y + (landingY - start.Y) * eased, start.Z), point) < 0.0001f);
        }
        Assert.Equal(InfiniteWalkingTransition.CreateTarget(current, original, pivot), transition.Current);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void RejectsInvalidPivot(float coordinate) =>
        Assert.Throws<ArgumentException>(() => new InfiniteWalkingTransition(RigidPose.Identity, RigidPose.Identity, new(coordinate, 0, 0)));

    [Fact]
    public void TargetReturnsHeightAndTiltWhileKeepingYaw()
    {
        var original = new RigidPose(
            Quaternion.CreateFromYawPitchRoll(0.1f, -0.2f, 0.05f),
            new(1, 0.25f, 2));
        var difference = Quaternion.CreateFromYawPitchRoll(0.8f, 0.6f, -0.4f);
        var current = new RigidPose(
            Quaternion.Normalize(difference * original.Orientation),
            new(5, 3, 7));

        var target = InfiniteWalkingTransition.CreateTarget(current, original);

        Assert.Equal(original.Position.Y, target.Position.Y);
        Assert.Equal(current.Position.X, target.Position.X);
        Assert.Equal(current.Position.Z, target.Position.Z);
        var remaining = Quaternion.Normalize(
            target.Orientation * Quaternion.Conjugate(original.Orientation));
        Assert.True(MathF.Abs(remaining.X) < 0.0001f);
        Assert.True(MathF.Abs(remaining.Z) < 0.0001f);
    }

    [Fact]
    public void AdvanceUsesOneSecondSmoothTransitionAndEndsExactlyAtTarget()
    {
        var current = new RigidPose(
            Quaternion.CreateFromYawPitchRoll(0.4f, 0.8f, 0.3f),
            new(1, 2, 3));
        var transition = new InfiniteWalkingTransition(current, RigidPose.Identity);

        var halfway = transition.Advance(0.5f);
        Assert.Equal(1, halfway.Position.Y, 4);
        Assert.False(transition.IsComplete);

        var complete = transition.Advance(0.5f);
        Assert.True(transition.IsComplete);
        Assert.True(InfiniteWalkingTransition.CreateTarget(current, RigidPose.Identity)
            .NearlyEquals(complete));
    }
}
