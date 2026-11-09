using System.Numerics;

namespace FlugelKranz.Core;

public sealed partial class FreeFlightManipulator
{
    // Value-type groups owned by one manipulator; no per-frame allocation or shared state.
    private struct ManipulationInputs
    {
        public bool LeftDragArmed, RightDragArmed, LeftTurnArmed, RightTurnArmed;
        public byte DragHands, TurnHands;
    }

    private struct DragState
    {
        public Vector3 Velocity;
        public Quaternion ReferenceOrientation;
        public Vector3 AnchorInRoot, PreviousPosition;
        public bool AccelerationBoostActive;
    }

    private struct TurnState
    {
        public Quaternion Anchor, StartOrientation, StartOffsetOrientation;
        public Vector3 TwoHandAxis, TwoHandPivot;
        public Quaternion PreviousTwoHandOrientation;
        public bool UseTwoHandPivotForInertia;
        public Vector3 Velocity;
        public RigidPose PreviousTarget;
    }

    private struct InertiaState
    {
        public Vector3 Velocity;
        public float BrakeElapsed, BrakeFactor, ExemptionSeconds;
    }
}
