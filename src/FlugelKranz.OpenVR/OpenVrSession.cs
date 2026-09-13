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
    private InputFrame lastFrame;
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
            actionSets = [new() { ulActionSet = set }];
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
        Check(input.UpdateActionState(actionSets, (uint)Marshal.SizeOf<VRActiveActionSet_t>()), "UpdateActionState");
        system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0, poses);
        var head = poses[Vr.k_unTrackedDeviceIndex_Hmd];
        bool tracked = head.bDeviceIsConnected && head.bPoseIsValid;
        // Read every device from one raw snapshot. Never mix cached pose-action
        // coordinates with the unmodified physical pose used by the motion engine.
        lastFrame = new(tracked ? OpenVrPose.FromMatrix(head.mDeviceToAbsoluteTracking) : RigidPose.Identity,
            tracked, ReadHand("left", ETrackedControllerRole.LeftHand), ReadHand("right", ETrackedControllerRole.RightHand));
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

    public void PreviewStanding(RigidPose standingToRaw)
    {
        var matrix = OpenVrPose.ToMatrix(standingToRaw);
        chaperone.SetWorkingStandingZeroPoseToRawTrackingPose(ref matrix);
        chaperone.ShowWorkingSetPreview();
    }

    public void OpenBindings() => Check(input.OpenBindingUI(AppKey, actionSets[0].ulActionSet, 0, true), "OpenBindingUI");
    public void HidePreview() => chaperone.HideWorkingSetPreview();
    public string DescribeInput() => $"left: Drag={lastFrame.Left.Drag}, Turn={lastFrame.Left.Turn}; right: Drag={lastFrame.Right.Drag}, Turn={lastFrame.Right.Turn}";

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
