using System.Numerics;

namespace FlugelKranz.Core;

/// <summary>Rotation calculations shared by flight manipulation and mode transitions.</summary>
internal static class RotationMath
{
    internal static Quaternion IntegrateRotation(Quaternion rotation, Vector3 velocity, float dt)
    {
        float speed = velocity.Length();
        if (speed <= 0 || dt <= 0)
            return rotation;

        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(velocity / speed, speed * dt) * rotation);
    }

    internal static Vector3 RotationVelocity(Quaternion from, Quaternion to, float dt)
    {
        var delta = Quaternion.Normalize(to * Quaternion.Conjugate(from));
        if (delta.W < 0)
            delta = -delta;

        float angle = 2 * MathF.Acos(Math.Clamp(delta.W, -1, 1));
        float sine = MathF.Sqrt(MathF.Max(0, 1 - delta.W * delta.W));
        return sine < 0.00001f
            ? Vector3.Zero
            : new Vector3(delta.X, delta.Y, delta.Z) / sine * (angle / dt);
    }

    internal static Quaternion TwistAroundAxis(Quaternion rotation, Vector3 axis)
    {
        axis = SafeDirection(axis);
        if (axis.LengthSquared() <= 0)
            return Quaternion.Identity;

        var imaginary = new Vector3(rotation.X, rotation.Y, rotation.Z);
        var projected = axis * Vector3.Dot(imaginary, axis);
        var twist = new Quaternion(projected, rotation.W);
        return twist.LengthSquared() <= 0.0000000001f
            ? Quaternion.Identity
            : Quaternion.Normalize(twist);
    }

    internal static float RotationAngleAroundAxis(Quaternion rotation, Vector3 axis)
    {
        axis = SafeDirection(axis);
        if (axis.LengthSquared() <= 0)
            return 0;

        rotation = Quaternion.Normalize(rotation);
        if (rotation.W < 0)
            rotation = -rotation;

        var imaginary = new Vector3(rotation.X, rotation.Y, rotation.Z);
        float imaginaryLength = imaginary.Length();
        if (imaginaryLength <= 0.0000001f)
            return 0;

        float angle = 2 * MathF.Atan2(imaginaryLength, MathF.Max(0, rotation.W));
        return Vector3.Dot(imaginary / imaginaryLength * angle, axis);
    }

    internal static Quaternion Average(Quaternion left, Quaternion right)
    {
        if (Quaternion.Dot(left, right) < 0)
            right = -right;
        return Quaternion.Normalize(Quaternion.Slerp(left, right, 0.5f));
    }

    internal static Vector3 SafeDirection(Vector3 value) =>
        value.LengthSquared() <= 0.0000000001f ? Vector3.Zero : Vector3.Normalize(value);
}
