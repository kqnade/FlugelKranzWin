using System.Numerics;

namespace FlugelKranz.Core;

/// <summary>Physical head deflection commands angular velocity, never virtual pose feedback.</summary>
public sealed class HeadPilot
{
    private bool armed, previousHeld, longPressTriggered;
    private float holdSeconds;
    public bool Active { get; private set; }
    private Quaternion neutral;
    public void Release() { armed = previousHeld = longPressTriggered = Active = false; holdSeconds = 0; }

    public (Vector3 AngularVelocity, Vector3 Acceleration, bool ResetInertia) Read(InputFrame frame, RigidPose offset, float dt)
    {
        if (!frame.PilotAvailable || !frame.HeadTracked || !frame.Head.IsValid || frame.MotionSuspended)
        {
            Release();
            return default;
        }
        var stick = frame.PilotStick;
        if (!float.IsFinite(stick.X) || !float.IsFinite(stick.Y)) { Release(); return default; }
        if (!armed)
        {
            armed = !frame.PilotHeld && MathF.Abs(stick.Y) <= 0.15f;
            return default;
        }
        bool resetInertia = false;
        if (frame.PilotHeld)
        {
            if (!previousHeld) { holdSeconds = 0; longPressTriggered = false; }
            else holdSeconds += float.IsFinite(dt) ? Math.Clamp(dt, 0, 0.1f) : 0;
            if (holdSeconds >= 0.5f && !longPressTriggered)
            {
                longPressTriggered = true;
                resetInertia = true;
            }
        }
        else if (previousHeld && !longPressTriggered)
        {
            Active = !Active;
            neutral = frame.Head.Orientation;
        }
        previousHeld = frame.PilotHeld;
        Vector3 angular = Vector3.Zero;
        if (Active)
        {
            var delta = Quaternion.Normalize(Quaternion.Conjugate(neutral) * frame.Head.Orientation);
            if (delta.W < 0) delta = new(-delta.X, -delta.Y, -delta.Z, -delta.W);
            var axis = new Vector3(delta.X, delta.Y, delta.Z);
            float length = axis.Length();
            float angle = 2 * MathF.Atan2(length, Math.Clamp(delta.W, 0, 1));
            float amount = Math.Clamp((angle - 3 * MathF.PI / 180) / (22 * MathF.PI / 180), 0, 1);
            if (length > 0.000001f)
                angular = Vector3.Transform(axis / length, offset.Orientation * neutral) * (amount * amount * MathF.PI / 2);
        }
        float magnitude = Math.Min(MathF.Abs(stick.Y), 1);
        Vector3 acceleration = Vector3.Zero;
        if (magnitude > 0.15f)
        {
            acceleration = Vector3.Transform(new Vector3(0, 0, -MathF.Sign(stick.Y)),
                offset.Orientation * frame.Head.Orientation) * ((magnitude - 0.15f) / 0.85f * 4);
        }
        return (angular, acceleration, resetInertia);
    }
}
