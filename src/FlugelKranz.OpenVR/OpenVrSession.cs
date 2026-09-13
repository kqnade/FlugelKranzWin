using System.Numerics;
using System.Runtime.InteropServices;
using FlugelKranz.Core;
using FlugelKranz.OpenXR;
using Valve.VR;
using Vr = Valve.VR.OpenVR;

namespace FlugelKranz.OpenVR;

internal sealed class OpenVrSession : IOpenVrSession
{
    private readonly CVRSystem system;
    private readonly CVRInput input;
    private readonly CVRChaperoneSetup chaperone;
    private readonly Func<ValveIndexInputSettings> settings;
    private readonly Dictionary<string, ulong> handles = [];
    private readonly VRActiveActionSet_t[] actionSets;
    private readonly TrackedDevicePose_t[] poses = new TrackedDevicePose_t[Vr.k_unMaxTrackedDeviceCount];
    private bool disposed;
    private readonly Dictionary<string, (InputPoseActionData_t Pose, InputDigitalActionData_t ThumbRest,
        InputDigitalActionData_t Trigger, uint Device)> handDiagnostics = [];

    public OpenVrSession(Func<ValveIndexInputSettings>? settings)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("SteamVR バックエンドは Windows x64 専用です。");
        this.settings = settings ?? (() => ValveIndexInputSettings.Default);
        EVRInitError error = EVRInitError.None;
        system = Vr.Init(ref error, EVRApplicationType.VRApplication_Overlay);
        if (error != EVRInitError.None)
            throw new InvalidOperationException($"SteamVR 接続失敗: {error}。SteamVR を起動してください。");
        try
        {
            input = Vr.Input ?? throw new InvalidOperationException("SteamVR Input が利用できません。");
            chaperone = Vr.ChaperoneSetup ?? throw new InvalidOperationException("Chaperone が利用できません。");
            Check(input.SetActionManifestPath(Path.Combine(AppContext.BaseDirectory, "Actions", "actions.json")), "SetActionManifestPath");
            ulong set = 0;
            Check(input.GetActionSetHandle("/actions/flight", ref set), "GetActionSetHandle");
            actionSets = [new() { ulActionSet = set }];
            foreach (var hand in new[] { "left", "right" })
                foreach (var control in new[] { "pose", "trackpad", "force", "touch", "thumbrest", "trigger" })
                {
                    string name = hand + "_" + control;
                    ulong handle = 0;
                    Check(input.GetActionHandle("/actions/flight/in/" + name, ref handle), "GetActionHandle " + name);
                    handles.Add(name, handle);
                }
        }
        catch { Vr.Shutdown(); throw; }
    }

    public RigidPose ReadWorkingStanding()
    {
        HmdMatrix34_t matrix = default;
        if (!chaperone.GetWorkingStandingZeroPoseToRawTrackingPose(ref matrix))
            throw new InvalidOperationException("Standing 原点を取得できません。SteamVR のルーム設定を確認してください。");
        return OpenVrPose.FromMatrix(matrix);
    }

    public InputFrame ReadRaw()
    {
        VREvent_t vrEvent = default;
        while (system.PollNextEvent(ref vrEvent, (uint)Marshal.SizeOf<VREvent_t>()))
            if (vrEvent.eventType == (uint)EVREventType.VREvent_Quit)
                throw new InvalidOperationException("SteamVR が終了しました。");
        Check(input.UpdateActionState(actionSets, (uint)Marshal.SizeOf<VRActiveActionSet_t>()), "UpdateActionState");
        system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0, poses);
        var head = poses[Vr.k_unTrackedDeviceIndex_Hmd];
        bool tracked = head.bDeviceIsConnected && head.bPoseIsValid;
        return new(tracked ? OpenVrPose.FromMatrix(head.mDeviceToAbsoluteTracking) : RigidPose.Identity,
            tracked, ReadHand("left", ControllerHand.Left), ReadHand("right", ControllerHand.Right));
    }

    private HandSample ReadHand(string name, ControllerHand hand)
    {
        InputPoseActionData_t pose = default;
        Check(input.GetPoseActionDataRelativeToNow(handles[name + "_pose"],
            ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0,
            ref pose, (uint)Marshal.SizeOf<InputPoseActionData_t>(), 0), "GetPoseActionData " + name);
        var pad = Analog(name + "_trackpad");
        var force = Analog(name + "_force");
        var touch = Digital(name + "_touch");
        var rest = Digital(name + "_thumbrest");
        var trigger = Digital(name + "_trigger");
        var actions = ControllerInputMapping.Map(hand, new(
            new Vector2(pad.x, pad.y), pad.bActive && touch.bActive && touch.bState,
            force.bActive ? force.x : float.NaN,
            rest.bState, trigger.bState, rest.bActive && trigger.bActive), settings());
        bool tracked = pose.bActive && pose.pose.bDeviceIsConnected && pose.pose.bPoseIsValid;
        uint device = system.GetTrackedDeviceIndexForControllerRole(hand == ControllerHand.Left
            ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);
        handDiagnostics[name] = (pose, rest, trigger, device);
        return new(tracked ? OpenVrPose.FromMatrix(pose.pose.mDeviceToAbsoluteTracking) : RigidPose.Identity,
            actions.Drag ? 1 : 0, actions.Turn ? 1 : 0, actions.DpadDown ? 1 : 0, tracked)
        { TrackpadForceActive = force.bActive, TrackpadForce = force.x };
    }

    private InputAnalogActionData_t Analog(string name)
    {
        InputAnalogActionData_t data = default;
        Check(input.GetAnalogActionData(handles[name], ref data, (uint)Marshal.SizeOf<InputAnalogActionData_t>(), 0), name);
        return data;
    }

    private InputDigitalActionData_t Digital(string name)
    {
        InputDigitalActionData_t data = default;
        Check(input.GetDigitalActionData(handles[name], ref data, (uint)Marshal.SizeOf<InputDigitalActionData_t>(), 0), name);
        return data;
    }

    public void PreviewStanding(RigidPose standingToRaw)
    {
        var matrix = OpenVrPose.ToMatrix(standingToRaw);
        chaperone.SetWorkingStandingZeroPoseToRawTrackingPose(ref matrix);
        chaperone.ShowWorkingSetPreview();
    }

    public void HidePreview() => chaperone.HideWorkingSetPreview();
    public string DescribeInput() => string.Join(Environment.NewLine, handDiagnostics.Select(entry =>
    {
        var (pose, rest, trigger, device) = entry.Value;
        string rawState = device < poses.Length
            ? $"connected={poses[device].bDeviceIsConnected}, valid={poses[device].bPoseIsValid}"
            : "role 未割当";
        return $"{entry.Key}: raw({rawState}), action(active={pose.bActive}, valid={pose.pose.bPoseIsValid}), thumbrest(active={rest.bActive}, touch={rest.bState}), trigger(active={trigger.bActive}, touch={trigger.bState})";
    }));

    private static void Check(EVRInputError error, string operation)
    {
        if (error != EVRInputError.None)
            throw new InvalidOperationException($"SteamVR Input {operation}: {error}");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Vr.Shutdown();
    }
}
