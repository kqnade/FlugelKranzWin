using System.Numerics;

namespace FlugelKranz.Core;

/// <summary>Returns free-flight height and tilt to the infinite-walking baseline.</summary>
public sealed class InfiniteWalkingTransition
{
    public const float DurationSeconds = 1;
    private readonly RigidPose start;
    private readonly RigidPose target;
    private readonly Vector3 turnPivot;
    private readonly Vector3 startPivot;
    private readonly Vector3 targetPivot;
    private float elapsed;

    public InfiniteWalkingTransition(RigidPose current, RigidPose original, Vector3 turnPivot = default)
    {
        if (!current.IsValid || !original.IsValid)
            throw new ArgumentException("Mode transition poses must be valid.");

        start = current;
        target = CreateTarget(current, original, turnPivot);
        this.turnPivot = turnPivot;
        startPivot = current.Transform(turnPivot);
        targetPivot = target.Transform(turnPivot);
        Current = current;
    }

    public bool IsComplete => elapsed >= DurationSeconds;
    public RigidPose Current { get; private set; }

    public RigidPose Advance(float elapsedSeconds)
    {
        if (float.IsFinite(elapsedSeconds) && elapsedSeconds > 0)
            elapsed = MathF.Min(DurationSeconds, elapsed + elapsedSeconds);
        if (IsComplete)
            return Current = target;
        float progress = elapsed / DurationSeconds;
        float eased = progress * progress * (3 - 2 * progress);
        Quaternion orientation = Quaternion.Normalize(Quaternion.Slerp(start.Orientation, target.Orientation, eased));
        Current = new(orientation,
            Vector3.Lerp(startPivot, targetPivot, eased) - Vector3.Transform(turnPivot, orientation));
        return Current;
    }

    public static RigidPose CreateTarget(RigidPose current, RigidPose original, Vector3 turnPivot = default)
    {
        if (!current.IsValid || !original.IsValid ||
            !float.IsFinite(turnPivot.X) || !float.IsFinite(turnPivot.Y) || !float.IsFinite(turnPivot.Z))
            throw new ArgumentException("Mode transition poses must be valid.");
        Quaternion difference = Quaternion.Normalize(
            current.Orientation * Quaternion.Conjugate(original.Orientation));
        Quaternion yaw = FreeFlightManipulator.TwistAroundAxis(difference, Vector3.UnitY);
        Quaternion orientation = Quaternion.Normalize(yaw * original.Orientation);
        // Offset maps physical Standing coordinates into the flown Standing space.
        // Keep the captured pivot's X/Z while restoring its connection-baseline Y.
        Vector3 landingPivot = current.Transform(turnPivot);
        landingPivot.Y = original.Transform(turnPivot).Y;
        Vector3 position = landingPivot - Vector3.Transform(turnPivot, orientation);
        position.Y = original.Position.Y;
        return new(orientation, position);
    }
}
