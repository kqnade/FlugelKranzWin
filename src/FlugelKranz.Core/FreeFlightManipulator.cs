using System.Numerics;
using static FlugelKranz.Core.RotationMath;

namespace FlugelKranz.Core;

/// <summary>Consumes poses in the unmodified physical tracking origin, never transformed XR poses.</summary>
public sealed partial class FreeFlightManipulator
{
    // Stop feeding imperceptibly small per-update changes to the runtime. The
    // threshold is applied to displacement (or radians), so it follows the
    // actual update interval rather than an arbitrary velocity value.
    private static readonly FlightMotionSettings DirectManipulationSettings =
        FlightMotionSettings.Default with
        {
            HeadPilotEnabled = false,
            TurnOrigin = TurnOrigin.TrackedElementsMidpoint,
            InertiaCutoffEnabled = false,
            InertiaAccelerationBoostEnabled = false,
            DragAccelerationMultiplier = 0,
            TurnAccelerationMultiplier = 0,
            DragSmoothSeconds = 0,
            TurnSmoothSeconds = 0
        };
    private const byte LeftHand = 1;
    private const byte RightHand = 2;
    private const byte BothHands = LeftHand | RightHand;
    private const float MaximumStepSeconds = 0.1f;
    private readonly HeadPilot pilot = new();
    private bool pilotWasEnabled;
    private ManipulationInputs inputs;
    private DragState drag;
    private TurnState turn;
    private InertiaState linear = new() { BrakeFactor = 1 };
    private InertiaState angular = new() { BrakeFactor = 1 };
    private RigidPose targetOffset;
    private bool positionTargetUsesDragSmoothing;
    public bool IsDragging => inputs.DragHands != 0;
    public bool IsTurning => inputs.TurnHands != 0;
    public bool IsHeadPiloting => pilot.Active;
    public bool HasLinearInertia => linear.Velocity.LengthSquared() > 0;
    public bool HasAngularInertia => angular.Velocity.LengthSquared() > 0;
    public RigidPose Offset { get; private set; }

    public FreeFlightManipulator(RigidPose offset) => SetOffset(offset);

    public void Release()
    {
        pilot.Release();
        inputs.DragHands = inputs.TurnHands = 0;
        inputs.LeftDragArmed = inputs.RightDragArmed = inputs.LeftTurnArmed = inputs.RightTurnArmed = false;
        targetOffset = Offset;
        linear.Velocity = drag.Velocity = angular.Velocity = turn.Velocity = Vector3.Zero;
        drag.ReferenceOrientation = turn.StartOrientation = turn.StartOffsetOrientation = Quaternion.Identity;
        drag.AnchorInRoot = drag.PreviousPosition = turn.TwoHandAxis = turn.TwoHandPivot = Vector3.Zero;
        turn.PreviousTwoHandOrientation = Quaternion.Identity;
        turn.UseTwoHandPivotForInertia = false;
        positionTargetUsesDragSmoothing = false;
        drag.AccelerationBoostActive = false;
        linear.BrakeElapsed = angular.BrakeElapsed = 0;
        linear.BrakeFactor = angular.BrakeFactor = 1;
        linear.ExemptionSeconds = angular.ExemptionSeconds = 0;
    }

    public void SetOffset(RigidPose offset)
    {
        if (!offset.IsValid)
            throw new ArgumentException("Invalid space offset.", nameof(offset));

        Offset = targetOffset = offset;
        Release();
    }

    public RigidPose Update(InputFrame frame) => Update(frame, 0.01f, DirectManipulationSettings);

    public RigidPose Update(InputFrame frame, float elapsedSeconds, FlightMotionSettings settings)
    {
        settings = settings.Normalized();
        float sampleSeconds = float.IsFinite(elapsedSeconds) ? MathF.Max(0, elapsedSeconds) : 0;
        float dt = Math.Min(sampleSeconds, MaximumStepSeconds);
        if (!frame.HeadTracked || !frame.Head.IsValid)
        {
            pilot.Release();
            return Offset;
        }

        if (pilotWasEnabled != settings.HeadPilotEnabled)
        {
            Release();
            pilotWasEnabled = settings.HeadPilotEnabled;
        }
        var pilotFrame = frame;
        if (settings.HeadPilotEnabled)
            frame = frame with { Left = frame.Left with { Turn = 0 }, Right = frame.Right with { Turn = 0 } };
        var (dragHandsChanged, turnHandsChanged) = UpdateGripState(frame, settings);

        var pilotCommand = settings.HeadPilotEnabled ? pilot.Read(pilotFrame, targetOffset, dt) : default;
        if (settings.HeadPilotEnabled && pilot.Active) angular.Velocity = Vector3.Zero;
        if (pilotCommand.ResetInertia)
        {
            linear.Velocity = angular.Velocity = drag.Velocity = turn.Velocity = Vector3.Zero;
            linear.ExemptionSeconds = angular.ExemptionSeconds = 0;
            targetOffset = Offset;
            if (IsDragging) RebaseDrag(frame);
            return Offset;
        }
        bool thrusting = !IsDragging && pilotCommand.Acceleration.LengthSquared() > 0;
        if (thrusting)
        {
            linear.Velocity += pilotCommand.Acceleration * dt;
            if (linear.Velocity.Length() > 12) linear.Velocity = Vector3.Normalize(linear.Velocity) * 12;
        }
        bool linearMotionActive = linear.Velocity.LengthSquared() > 0;
        bool angularMotionActive = angular.Velocity.LengthSquared() > 0;
        bool brakingAngularInertia = IsTurning || inputs.DragHands == BothHands;
        var orientationBefore = targetOffset.Orientation;
        AdvanceFreeInertia(frame, dt, !IsDragging, !brakingAngularInertia, settings, !thrusting);
        if (!IsDragging && settings.HeadPilotEnabled)
        {
            AdvanceTargetRotation(frame, pilotCommand.AngularVelocity, dt, settings with { TurnOrigin = TurnOrigin.Head });
            if (pilotCommand.AngularVelocity.LengthSquared() > 0) angularMotionActive = true;
        }
        AdvanceGrabInertia(frame, dt, sampleSeconds, settings);

        var target = GrabTarget(frame, settings);
        UpdateRawVelocities(
            frame,
            target,
            IsDragging && !dragHandsChanged,
            IsTurning && !turnHandsChanged,
            sampleSeconds);
        targetOffset = target;
        if (IsDragging || linearMotionActive)
            positionTargetUsesDragSmoothing = true;
        else if (IsTurning || angularMotionActive)
            positionTargetUsesDragSmoothing = false;
        RotateLinearInertia(
            orientationBefore,
            targetOffset.Orientation,
            settings.VectorRotationMultiplier);
        FollowTarget(frame, dt, pilot.Active && !IsDragging ? settings with { TurnOrigin = TurnOrigin.Head } : settings);
        return Offset;
    }

    private (bool DragChanged, bool TurnChanged) UpdateGripState(InputFrame frame, FlightMotionSettings settings)
    {
        if (!settings.DragEnabled)
        {
            if (IsDragging) { CancelDrag(); targetOffset = Offset; }
            inputs.DragHands = 0;
            inputs.LeftDragArmed = inputs.RightDragArmed = false;
        }
        byte previousDragHands = inputs.DragHands;
        byte previousTurnHands = inputs.TurnHands;
        inputs.DragHands = settings.DragEnabled ? ActiveHands(frame, true, previousDragHands) : (byte)0;
        inputs.TurnHands = ActiveHands(frame, false, previousTurnHands);
        if (previousDragHands == BothHands && inputs.DragHands is LeftHand or RightHand)
        {
            if (inputs.DragHands == LeftHand)
                inputs.LeftDragArmed = false;
            else
                inputs.RightDragArmed = false;
            inputs.DragHands = 0;
        }
        if (previousTurnHands == BothHands && inputs.TurnHands is LeftHand or RightHand)
        {
            if (inputs.TurnHands == LeftHand)
                inputs.LeftTurnArmed = false;
            else
                inputs.RightTurnArmed = false;
            inputs.TurnHands = 0;
        }
        bool dragHandsChanged = inputs.DragHands != previousDragHands;
        bool turnHandsChanged = inputs.TurnHands != previousTurnHands;
        bool beganDrag = previousDragHands == 0 && inputs.DragHands != 0;
        bool beganTurn = previousTurnHands == 0 && inputs.TurnHands != 0;
        bool beganTwoHandDrag = previousDragHands != BothHands && inputs.DragHands == BothHands;

        if (previousDragHands != 0 && inputs.DragHands == 0)
        {
            if (HandsUsable(frame, previousDragHands))
                FinishDrag(frame.Head, settings);
            else
                CancelDrag();
        }
        if (previousTurnHands != 0 && inputs.TurnHands == 0)
        {
            turn.UseTwoHandPivotForInertia = previousTurnHands == BothHands;
            if (HandsUsable(frame, previousTurnHands))
                FinishTurn(settings);
            else
                CancelTurn();
        }

        if (beganDrag)
        {
            linear.ExemptionSeconds = 0;
            linear.BrakeElapsed = 0;
            linear.BrakeFactor = 1;
            drag.AccelerationBoostActive = false;
        }
        if (beganTurn)
        {
            angular.ExemptionSeconds = 0;
            angular.BrakeElapsed = 0;
            angular.BrakeFactor = 1;
        }
        if (beganTwoHandDrag)
        {
            angular.ExemptionSeconds = 0;
            angular.BrakeElapsed = 0;
            angular.BrakeFactor = 1;
        }

        if (!IsDragging && !IsTurning)
        {
            // Smoothing may leave the presented offset behind targetOffset when
            // a grip is released. Start inertia from what is actually visible
            // instead of snapping to the unapplied target first.
            targetOffset = Offset;
        }

        bool activeHandsChanged =
            (dragHandsChanged && inputs.DragHands != 0) ||
            (turnHandsChanged && inputs.TurnHands != 0);
        if (activeHandsChanged)
        {
            // A new grip starts from the pose currently presented to the runtime,
            // discarding any unapplied smoothing remainder without a jump.
            targetOffset = Offset;
            if (IsDragging)
                RebaseDrag(frame);
            if (IsTurning)
                RebaseTurn(frame);
        }

        return (dragHandsChanged, turnHandsChanged);
    }

    private void AdvanceGrabInertia(InputFrame frame, float dt, float sampleSeconds, FlightMotionSettings settings)
    {
        if (IsDragging)
        {
            float controllerSpeed = ControllerMovementSpeed(frame, sampleSeconds);
            drag.AccelerationBoostActive =
                settings.InertiaAccelerationBoostEnabled &&
                controllerSpeed > settings.DragCutoffMetresPerSecond;
            if (!drag.AccelerationBoostActive)
                ApplyGrabBrake(
                    ref linear.Velocity,
                    ref linear.BrakeElapsed,
                    ref linear.BrakeFactor,
                    dt,
                    settings.BrakeRampSeconds);
            Vector3 dragInertiaStep = linear.Velocity * dt;
            drag.AnchorInRoot += dragInertiaStep;
            ApplyDeceleration(
                ref linear.Velocity,
                ref linear.ExemptionSeconds,
                dt,
                settings,
                settings.InertiaStopDisplacementMetres);
        }
        if (IsTurning)
        {
            ApplyGrabBrake(
                ref angular.Velocity,
                ref angular.BrakeElapsed,
                ref angular.BrakeFactor,
                dt,
                settings.BrakeRampSeconds);
            if (inputs.TurnHands == BothHands)
                turn.StartOffsetOrientation = IntegrateRotation(turn.StartOffsetOrientation, angular.Velocity, dt);
            else
                turn.Anchor = IntegrateRotation(turn.Anchor, angular.Velocity, dt);
            ApplyDeceleration(
                ref angular.Velocity,
                ref angular.ExemptionSeconds,
                dt,
                settings,
                settings.InertiaStopDisplacementMetres);
        }
        else if (inputs.DragHands == BothHands)
        {
            ApplyGrabBrake(
                ref angular.Velocity,
                ref angular.BrakeElapsed,
                ref angular.BrakeFactor,
                dt,
                settings.BrakeRampSeconds);
            AdvanceTargetRotation(frame, angular.Velocity, dt, settings);
            ApplyDeceleration(
                ref angular.Velocity,
                ref angular.ExemptionSeconds,
                dt,
                settings,
                settings.InertiaStopDisplacementMetres);
        }

    }

    private byte ActiveHands(InputFrame frame, bool drag, byte previous)
    {
        byte result = 0;
        bool leftHeld = (previous & LeftHand) != 0;
        bool rightHeld = (previous & RightHand) != 0;
        bool left = drag
            ? Held(frame.Left, frame.Left.Drag, ref inputs.LeftDragArmed, leftHeld)
            : Held(frame.Left, frame.Left.Turn, ref inputs.LeftTurnArmed, leftHeld);
        bool right = drag
            ? Held(frame.Right, frame.Right.Drag, ref inputs.RightDragArmed, rightHeld)
            : Held(frame.Right, frame.Right.Turn, ref inputs.RightTurnArmed, rightHeld);
        if (left)
            result |= LeftHand;
        if (right)
            result |= RightHand;
        return result;
    }

    private void RebaseDrag(InputFrame frame)
    {
        drag.ReferenceOrientation = targetOffset.Orientation;
        drag.PreviousPosition = HandPosition(frame, inputs.DragHands);
        drag.AnchorInRoot = targetOffset.Transform(drag.PreviousPosition);
        drag.Velocity = Vector3.Zero;
    }

    private void RebaseTurn(InputFrame frame)
    {
        turn.StartOrientation = HandOrientation(frame, inputs.TurnHands);
        turn.StartOffsetOrientation = targetOffset.Orientation;
        turn.Anchor = Quaternion.Normalize(targetOffset.Orientation * turn.StartOrientation);
        turn.TwoHandAxis = inputs.TurnHands == BothHands
            ? SafeDirection(frame.Right.Pose.Position - frame.Left.Pose.Position)
            : Vector3.Zero;
        turn.TwoHandPivot = inputs.TurnHands == BothHands
            ? HandPosition(frame, BothHands)
            : Vector3.Zero;
        turn.PreviousTwoHandOrientation = inputs.TurnHands == BothHands
            ? HandOrientation(frame, BothHands)
            : Quaternion.Identity;
        turn.UseTwoHandPivotForInertia = inputs.TurnHands == BothHands;
        turn.Velocity = Vector3.Zero;
        turn.PreviousTarget = targetOffset;
    }

    private void FollowTarget(
        InputFrame frame,
        float elapsedSeconds,
        FlightMotionSettings settings)
    {
        if (!IsDragging && !IsTurning)
        {
            Offset = targetOffset;
            return;
        }

        float positionSmoothSeconds = positionTargetUsesDragSmoothing
            ? settings.DragSmoothSeconds
            : settings.TurnSmoothSeconds;
        Quaternion orientation = MotionSmoothing.Follow(
            Offset.Orientation,
            targetOffset.Orientation,
            IsTurning ? settings.TurnSmoothSeconds : 0,
            elapsedSeconds);
        Vector3 position;
        if (IsDragging)
        {
            Vector3 dragPoint = HandPosition(frame, inputs.DragHands);
            Vector3 dragPointInRoot = MotionSmoothing.Follow(
                Offset.Transform(dragPoint),
                drag.AnchorInRoot,
                settings.DragSmoothSeconds,
                elapsedSeconds);
            position = dragPointInRoot - Vector3.Transform(dragPoint, orientation);
        }
        else if (positionTargetUsesDragSmoothing)
        {
            position = MotionSmoothing.Follow(
                Offset.Position,
                targetOffset.Position,
                positionSmoothSeconds,
                elapsedSeconds);
        }
        else
        {
            Vector3 pivot = TurnPivot(frame, settings);
            Vector3 pivotInRoot = MotionSmoothing.Follow(
                Offset.Transform(pivot),
                targetOffset.Transform(pivot),
                positionSmoothSeconds,
                elapsedSeconds);
            position = pivotInRoot - Vector3.Transform(pivot, orientation);
        }
        Offset = new(orientation, position);
    }

    private void UpdateRawVelocities(
        InputFrame frame,
        RigidPose target,
        bool dragging,
        bool turning,
        float sampleSeconds)
    {
        if (dragging && sampleSeconds > 0)
        {
            Vector3 current = HandPosition(frame, inputs.DragHands);
            var controllerDelta = current - drag.PreviousPosition;
            drag.Velocity = -Vector3.Transform(controllerDelta, drag.ReferenceOrientation) / sampleSeconds;
            drag.PreviousPosition = current;
        }

        if (turning && sampleSeconds > 0)
        {
            turn.Velocity = RotationVelocity(
                turn.PreviousTarget.Orientation,
                target.Orientation,
                sampleSeconds);
            turn.PreviousTarget = target;
        }
    }

    private float ControllerMovementSpeed(InputFrame frame, float sampleSeconds)
    {
        if (sampleSeconds <= 0)
            return drag.Velocity.Length();

        return Vector3.Distance(HandPosition(frame, inputs.DragHands), drag.PreviousPosition) / sampleSeconds;
    }

    private static void ApplyGrabBrake(
        ref Vector3 velocity,
        ref float elapsed,
        ref float previousFactor,
        float deltaSeconds,
        float rampSeconds)
    {
        if (deltaSeconds <= 0 || velocity.LengthSquared() <= 0)
            return;

        if (rampSeconds <= 0)
        {
            velocity = Vector3.Zero;
            previousFactor = 0;
            elapsed = 0;
            return;
        }

        elapsed = MathF.Min(rampSeconds, elapsed + deltaSeconds);
        float progress = elapsed / rampSeconds;
        float easedProgress = progress * progress * (3 - 2 * progress);
        float targetFactor = MathF.Min(previousFactor, MathF.Max(0, 1 - easedProgress));
        if (previousFactor > 0)
            velocity *= targetFactor / previousFactor;
        previousFactor = targetFactor;
    }

    private void RotateLinearInertia(Quaternion from, Quaternion to, float multiplier)
    {
        if (linear.Velocity.LengthSquared() <= 0 || multiplier <= 0)
            return;

        var delta = Quaternion.Normalize(to * Quaternion.Conjugate(from));
        if (delta.W < 0)
            delta = -delta;

        var applied = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, delta, multiplier));
        linear.Velocity = Vector3.Transform(linear.Velocity, applied);
    }

    private RigidPose GrabTarget(InputFrame frame, FlightMotionSettings settings)
    {
        var rotation = targetOffset.Orientation;
        var translation = targetOffset.Position;
        if (IsTurning)
        {
            rotation = TurnTargetOrientation(frame);
            Vector3 pivot = TurnPivot(frame, settings);
            translation = targetOffset.Transform(pivot) - Vector3.Transform(pivot, rotation);
        }
        if (IsDragging)
            translation = drag.AnchorInRoot -
                Vector3.Transform(HandPosition(frame, inputs.DragHands), rotation);

        return new(rotation, translation);
    }

    private Quaternion TurnTargetOrientation(InputFrame frame)
    {
        if (inputs.TurnHands != BothHands)
        {
            Quaternion singleHandOrientation = HandOrientation(frame, inputs.TurnHands);
            return Quaternion.Normalize(turn.Anchor * Quaternion.Conjugate(singleHandOrientation));
        }

        Quaternion current = HandOrientation(frame, BothHands);
        if (Quaternion.Dot(current, turn.PreviousTwoHandOrientation) < 0)
            current = -current;

        // Keep the pole axis captured at the moment both hands start turning.
        // Reprojecting the accumulated pose onto a moving hand line can turn
        // tracking noise or a large pose change into an unintended spin.
        Vector3 axis = turn.TwoHandAxis;
        if (axis.LengthSquared() <= 0)
        {
            turn.PreviousTwoHandOrientation = current;
            return turn.StartOffsetOrientation;
        }

        Quaternion controllerDelta = Quaternion.Normalize(current * Quaternion.Conjugate(turn.PreviousTwoHandOrientation));
        float twistAngle = RotationAngleAroundAxis(controllerDelta, axis);
        turn.PreviousTwoHandOrientation = current;
        if (MathF.Abs(twistAngle) > 0)
        {
            Quaternion inverseTwist = Quaternion.CreateFromAxisAngle(axis, -twistAngle);
            turn.StartOffsetOrientation = Quaternion.Normalize(
                turn.StartOffsetOrientation * inverseTwist);
        }
        return turn.StartOffsetOrientation;
    }

    private Vector3 TurnPivot(InputFrame frame, FlightMotionSettings settings)
    {
        if (IsDragging)
            return HandPosition(frame, inputs.DragHands);
        if (inputs.TurnHands == BothHands ||
            (inputs.TurnHands == 0 && turn.UseTwoHandPivotForInertia && angular.Velocity.LengthSquared() > 0))
            return turn.TwoHandPivot;
        return GetSingleHandTurnPivot(frame, settings);
    }

    internal Vector3 GetSingleHandTurnPivot(InputFrame frame, FlightMotionSettings settings)
    {
        if (IsDragging)
            return HandPosition(frame, inputs.DragHands);
        if (settings.TurnOrigin == TurnOrigin.Head)
            return frame.Head.Position;

        Vector3 sum = frame.Head.Position;
        int count = 1;
        if (Usable(frame.Left))
        {
            sum += frame.Left.Pose.Position;
            count++;
        }
        if (Usable(frame.Right))
        {
            sum += frame.Right.Pose.Position;
            count++;
        }
        return sum / count;
    }

    internal Vector3 GetCurrentTurnPivot(InputFrame frame, FlightMotionSettings settings) =>
        TurnPivot(frame, settings.Normalized());

    private void AdvanceTargetRotation(
        InputFrame frame,
        Vector3 velocity,
        float elapsedSeconds,
        FlightMotionSettings settings)
    {
        if (velocity.LengthSquared() <= 0 || elapsedSeconds <= 0)
            return;

        Vector3 pivot = TurnPivot(frame, settings);
        Vector3 pivotInRoot = targetOffset.Transform(pivot);
        Quaternion orientation = IntegrateRotation(
            targetOffset.Orientation,
            velocity,
            elapsedSeconds);
        targetOffset = new(
            orientation,
            pivotInRoot - Vector3.Transform(pivot, orientation));
    }

    private void AdvanceFreeInertia(
        InputFrame frame,
        float dt,
        bool move,
        bool turn,
        FlightMotionSettings settings,
        bool dampTranslation)
    {
        if (dt <= 0)
            return;

        var rotation = targetOffset.Orientation;
        var translation = targetOffset.Position;
        if (turn && angular.Velocity.LengthSquared() > 0)
        {
            Vector3 pivot = TurnPivot(frame, settings);
            var pivotInRoot = targetOffset.Transform(pivot);
            rotation = IntegrateRotation(rotation, angular.Velocity, dt);
            translation = pivotInRoot - Vector3.Transform(pivot, rotation);
        }

        if (move)
            translation += linear.Velocity * dt;

        targetOffset = new(rotation, translation);
        if (move && dampTranslation)
            ApplyDeceleration(
                ref linear.Velocity,
                ref linear.ExemptionSeconds,
                dt,
                settings,
                settings.InertiaStopDisplacementMetres);
        if (turn)
            ApplyDeceleration(
                ref angular.Velocity,
                ref angular.ExemptionSeconds,
                dt,
                settings,
                settings.InertiaStopDisplacementMetres);
    }

    private void FinishDrag(RigidPose head, FlightMotionSettings settings)
    {
        bool hasUsableAcceleration = drag.Velocity.LengthSquared() > 0.0000000001f &&
            (!settings.InertiaCutoffEnabled || drag.Velocity.Length() >= settings.DragCutoffMetresPerSecond);
        if (hasUsableAcceleration)
        {
            // drag.Velocity is in root space. Apply the reference-space rotation
            // to the physical HMD orientation before comparing their directions.
            var headInRoot = Quaternion.Normalize(Offset.Orientation * head.Orientation);
            var acceleration = ApplyDragAcceleration(drag.Velocity, headInRoot, settings);
            float speedBeforeBoost = linear.Velocity.Length();
            if (drag.AccelerationBoostActive && speedBeforeBoost > 0.0000000001f)
            {
                var boosted = linear.Velocity + acceleration;
                float boostReferenceSpeed = MathF.Max(speedBeforeBoost, acceleration.Length());
                float maximumSpeed = boostReferenceSpeed * settings.InertiaAccelerationBoostMaximumMultiplier;
                if (boosted.Length() > maximumSpeed && maximumSpeed > 0)
                    boosted = Vector3.Normalize(boosted) * maximumSpeed;
                linear.Velocity = boosted;
            }
            else
                linear.Velocity = acceleration;

            linear.ExemptionSeconds = ExemptionDuration(
                linear.Velocity.Length(),
                0.001f,
                settings.DragDecelerationExemptionDurationRatio,
                settings);
        }
        else if (linear.Velocity.LengthSquared() <= 0.0000000001f)
        {
            linear.Velocity = Vector3.Zero;
            linear.ExemptionSeconds = 0;
        }

        drag.AccelerationBoostActive = false;
        drag.Velocity = Vector3.Zero;
    }

    private static Vector3 ApplyDragAcceleration(
        Vector3 velocity,
        Quaternion playerOrientation,
        FlightMotionSettings settings)
    {
        var baseVelocity = velocity * settings.DragAccelerationMultiplier;
        if (baseVelocity.LengthSquared() <= 0 || settings.ZAccelerationMultiplier <= 1)
            return baseVelocity;

        var forward = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, playerOrientation));
        var direction = Vector3.Normalize(velocity);
        float alignment = MathF.Abs(Math.Clamp(Vector3.Dot(forward, direction), -1, 1));
        float zMultiplier = 1 + (settings.ZAccelerationMultiplier - 1) * alignment;
        var forwardVelocity = forward * Vector3.Dot(baseVelocity, forward);
        var lateralVelocity = baseVelocity - forwardVelocity;
        return lateralVelocity + forwardVelocity * zMultiplier;
    }

    private void FinishTurn(FlightMotionSettings settings)
    {
        if (settings.InertiaCutoffEnabled && turn.Velocity.Length() < settings.TurnCutoffRadiansPerSecond)
            angular.Velocity = Vector3.Zero;
        else
            angular.Velocity = turn.Velocity * settings.TurnAccelerationMultiplier;

        angular.ExemptionSeconds = ExemptionDuration(
            angular.Velocity.Length(),
            MathF.PI / 1800,
            settings.TurnDecelerationExemptionDurationRatio,
            settings);
    }

    private static void ApplyDeceleration(
        ref Vector3 velocity,
        ref float exemptionSeconds,
        float elapsedSeconds,
        FlightMotionSettings settings,
        float stopCutoff)
    {
        if (velocity.LengthSquared() <= 0 || elapsedSeconds <= 0)
            return;

        float exemptedSeconds = Math.Min(exemptionSeconds, elapsedSeconds);
        float regularSeconds = elapsedSeconds - exemptedSeconds;
        exemptionSeconds = MathF.Max(0, exemptionSeconds - elapsedSeconds);
        float exponent = settings.InertiaDecelerationPerSecond *
            (regularSeconds + exemptedSeconds * (1 - settings.DecelerationExemptionStrength));
        velocity *= MathF.Exp(-exponent);
        if (velocity.Length() * elapsedSeconds < stopCutoff)
            velocity = Vector3.Zero;
    }

    private static float ExemptionDuration(
        float speed,
        float terminalSpeed,
        float durationRatio,
        FlightMotionSettings settings)
    {
        if (!settings.DecelerationExemptionEnabled ||
            durationRatio <= 0 ||
            settings.InertiaDecelerationPerSecond <= 0 ||
            speed <= terminalSpeed)
            return 0;

        float positiveTerminalSpeed = MathF.Max(terminalSpeed, 0.00001f);
        float expectedSeconds = MathF.Log(speed / positiveTerminalSpeed) / settings.InertiaDecelerationPerSecond;
        return expectedSeconds * durationRatio;
    }

    private void CancelDrag()
    {
        linear.Velocity = drag.Velocity = Vector3.Zero;
        drag.AnchorInRoot = drag.PreviousPosition = Vector3.Zero;
        drag.AccelerationBoostActive = false;
        linear.BrakeElapsed = 0;
        linear.BrakeFactor = 1;
        linear.ExemptionSeconds = 0;
    }

    private void CancelTurn()
    {
        angular.Velocity = turn.Velocity = Vector3.Zero;
        angular.BrakeElapsed = 0;
        angular.BrakeFactor = 1;
        angular.ExemptionSeconds = 0;
        turn.UseTwoHandPivotForInertia = false;
    }

    private static Quaternion HandOrientation(InputFrame frame, byte hands) => hands switch
    {
        LeftHand => frame.Left.Pose.Orientation,
        RightHand => frame.Right.Pose.Orientation,
        BothHands => Average(frame.Left.Pose.Orientation, frame.Right.Pose.Orientation),
        _ => Quaternion.Identity
    };

    private static Vector3 HandPosition(InputFrame frame, byte hands) => hands switch
    {
        LeftHand => frame.Left.Pose.Position,
        RightHand => frame.Right.Pose.Position,
        BothHands => (frame.Left.Pose.Position + frame.Right.Pose.Position) * 0.5f,
        _ => Vector3.Zero
    };

    private static bool HandsUsable(InputFrame frame, byte hands) =>
        ((hands & LeftHand) == 0 || Usable(frame.Left)) &&
        ((hands & RightHand) == 0 || Usable(frame.Right));

    private static bool Held(HandSample hand, float value, ref bool armed, bool held)
    {
        if (!Usable(hand) || !float.IsFinite(value))
        {
            armed = false;
            return false;
        }

        if (value <= 0.35f)
        {
            armed = true;
            return false;
        }

        return armed && value >= (held ? 0.35f : 0.65f);
    }

    private static bool Usable(HandSample hand) => hand.IsTracked && hand.Pose.IsValid;
}
