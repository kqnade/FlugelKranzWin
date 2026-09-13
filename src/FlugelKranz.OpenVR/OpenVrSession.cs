using System.Runtime.InteropServices;
using FlugelKranz.Core;
using Valve.VR;
using Vr = Valve.VR.OpenVR;

namespace FlugelKranz.OpenVR;

internal sealed class OpenVrSession : IOpenVrSession
{
    public const string AppKey = "org.flugelkranz.windows";
    private readonly CVRSystem system;
    private readonly CVRInput input;
    private readonly CVRChaperoneSetup chaperone;
    private readonly Dictionary<string, ulong> handles = [];
    private readonly VRActiveActionSet_t[] actionSets;
    private readonly TrackedDevicePose_t[] poses = new TrackedDevicePose_t[Vr.k_unMaxTrackedDeviceCount];
    private readonly PilotInputGate pilotGate = new();
    private bool pilotRequested;
    private InputFrame lastFrame;
    private string pilotDiagnostics = "";
    private bool disposed;

    public OpenVrSession()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("SteamVR バックエンドは Windows x64 専用です。");
        EVRInitError error = EVRInitError.None;
        system = Vr.Init(ref error, EVRApplicationType.VRApplication_Overlay);
        if (error != EVRInitError.None)
            throw new InvalidOperationException($"SteamVR 接続失敗: {error}。SteamVR を起動してください。");
        try
        {
            input = Vr.Input ?? throw new InvalidOperationException("SteamVR Input が利用できません。");
            chaperone = Vr.ChaperoneSetup ?? throw new InvalidOperationException("Chaperone が利用できません。");
            var apps = Vr.Applications ?? throw new InvalidOperationException("SteamVR Applications が利用できません。");
            CheckApplication(apps.AddApplicationManifest(Path.Combine(AppContext.BaseDirectory, "manifest.vrmanifest"), false), "AddApplicationManifest");
            CheckApplication(apps.IdentifyApplication((uint)Environment.ProcessId, AppKey), "IdentifyApplication");
            Check(input.SetActionManifestPath(Path.Combine(AppContext.BaseDirectory, "Actions", "actions.json")), "SetActionManifestPath");
            ulong set = 0;
            Check(input.GetActionSetHandle("/actions/flight", ref set), "GetActionSetHandle");
            ulong pilotSet = 0;
            Check(input.GetActionSetHandle("/actions/pilot", ref pilotSet), "GetActionSetHandle pilot");
            actionSets = [new() { ulActionSet = set }, new() { ulActionSet = pilotSet }];
            foreach (string control in new[] { "thrust", "left_toggle", "right_toggle" })
            {
                ulong handle = 0;
                Check(input.GetActionHandle("/actions/pilot/in/" + control, ref handle), control);
                handles.Add(control, handle);
            }
            foreach (var hand in new[] { "left", "right" })
                foreach (var control in new[] { "drag", "turn", "reset_hold" })
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
        bool dashboard = (Vr.Overlay ?? throw new InvalidOperationException("SteamVR Overlay が利用できません。")).IsDashboardVisible();
        actionSets[1].nPriority = PilotInputGate.Priority(pilotRequested, dashboard);
        Check(input.UpdateActionState(actionSets, (uint)Marshal.SizeOf<VRActiveActionSet_t>()), "UpdateActionState");
        system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0, poses);
        var head = poses[Vr.k_unTrackedDeviceIndex_Hmd];
        bool tracked = head.bDeviceIsConnected && head.bPoseIsValid;
        // Read every device from one raw snapshot. Never mix cached pose-action
        // coordinates with the unmodified physical pose used by the motion engine.
        lastFrame = new(tracked ? OpenVrPose.FromMatrix(head.mDeviceToAbsoluteTracking) : RigidPose.Identity,
            tracked, ReadHand("left", ETrackedControllerRole.LeftHand), ReadHand("right", ETrackedControllerRole.RightHand));
        InputAnalogActionData_t thrust = default;
        Check(input.GetAnalogActionData(handles["thrust"], ref thrust, (uint)Marshal.SizeOf<InputAnalogActionData_t>(), 0), "thrust");
        var leftToggle = Digital("left_toggle");
        var rightToggle = Digital("right_toggle");
        bool available = thrust.bActive && (leftToggle.bActive || rightToggle.bActive) && tracked;
        var stick = new System.Numerics.Vector2(thrust.x, thrust.y);
        bool held = leftToggle.bActive && leftToggle.bState || rightToggle.bActive && rightToggle.bState;
        bool neutral = available && !held && float.IsFinite(stick.X) && float.IsFinite(stick.Y)
            && MathF.Abs(stick.Y) <= 0.15f && lastFrame.Left.Drag == 0 && lastFrame.Right.Drag == 0
            && lastFrame.Left.Turn == 0 && lastFrame.Right.Turn == 0
            && lastFrame.Left.DpadDown == 0 && lastFrame.Right.DpadDown == 0;
        bool suspended = pilotGate.Update(pilotRequested, dashboard, neutral);
        pilotDiagnostics = $"Pilot requested={pilotRequested} priority=0x{actionSets[1].nPriority:X} " +
            $"dashboard={dashboard} suspended={suspended} neutral={neutral} " +
            $"stickActive={thrust.bActive} y={thrust.y:F3} " +
            $"X(active/pressed)={leftToggle.bActive}/{leftToggle.bState} " +
            $"A(active/pressed)={rightToggle.bActive}/{rightToggle.bState} " +
            $"head={tracked} inputAvailable={system.IsInputAvailable()} " +
            $"scenePid={Vr.Compositor?.GetCurrentSceneFocusProcess()}";
        lastFrame = lastFrame with
        {
            PilotStick = stick, PilotHeld = held,
            PilotAvailable = pilotRequested && available && !suspended,
            MotionSuspended = suspended
        };
        return lastFrame;
    }

    private HandSample ReadHand(string name, ETrackedControllerRole role) => OpenVrFrame.Hand(
        poses, system.GetTrackedDeviceIndexForControllerRole(role),
        Digital(name + "_drag"), Digital(name + "_turn"), Digital(name + "_reset_hold"));

    private InputDigitalActionData_t Digital(string name)
    {
        InputDigitalActionData_t data = default;
        Check(input.GetDigitalActionData(handles[name], ref data, (uint)Marshal.SizeOf<InputDigitalActionData_t>(), 0), name);
        return data;
    }

    public void SetPilotInputEnabled(bool enabled)
    {
        if (enabled && !pilotRequested)
        {
            var settings = Vr.Settings ?? throw new InvalidOperationException("SteamVR Settings が利用できません。");
            EVRSettingsError error = EVRSettingsError.None;
            bool allowed = settings.GetBool("steamvr", "globalActionSetPriority", ref error);
            if (error != EVRSettingsError.None || !allowed)
                throw new InvalidOperationException("頭部操縦には SteamVR 開発者設定の Experimental overlay input overrides を有効にしてください。");
        }
        pilotRequested = enabled;
    }

    public void PreviewStanding(RigidPose standingToRaw)
    {
        var matrix = OpenVrPose.ToMatrix(standingToRaw);
        chaperone.SetWorkingStandingZeroPoseToRawTrackingPose(ref matrix);
        chaperone.ShowWorkingSetPreview();
    }

    public void OpenBindings() => Check(input.OpenBindingUI(AppKey, actionSets[0].ulActionSet, 0, true), "OpenBindingUI");
    public (uint Left, uint Right) ControllerDevices() => (
        system.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand),
        system.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand));
    public void HidePreview() => chaperone.HideWorkingSetPreview();
    public string DescribeInput() => $"left: Drag={lastFrame.Left.Drag}, Turn={lastFrame.Left.Turn}; right: Drag={lastFrame.Right.Drag}, Turn={lastFrame.Right.Turn}; {pilotDiagnostics}";

    private static void CheckApplication(EVRApplicationError error, string operation)
    {
        if (error != EVRApplicationError.None) throw new InvalidOperationException($"SteamVR {operation}: {error}");
    }

    private static void Check(EVRInputError error, string operation)
    {
        if (error != EVRInputError.None) throw new InvalidOperationException($"SteamVR Input {operation}: {error}");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Vr.Shutdown();
    }
}
