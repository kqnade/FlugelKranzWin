using FlugelKranz.Core;

namespace FlugelKranz.OpenVR;

/// <summary>Native boundary; all poses are right-handed, metres, +Y up, -Z forward.</summary>
public interface IOpenVrSession : IDisposable
{
    RigidPose ReadWorkingStanding();
    InputFrame ReadRaw();
    void PreviewStanding(RigidPose standingToRaw);
    void HidePreview();
    string DescribeInput() => "";
    void OpenBindings() => throw new NotSupportedException();
    (uint Left, uint Right) ControllerDevices() => (uint.MaxValue, uint.MaxValue);
}

public sealed class OpenVrFlightRuntime : IFlightRuntime, IReferenceSpaceOffsetProvider, IFlightBindings
{
    private readonly IOpenVrSession session;
    private readonly RigidPose originalStanding;
    private RigidPose expectedStanding;
    private bool previewOwned;
    private bool disposed;

    public RigidPose OriginalOffset => RigidPose.Identity;
    public RigidPose CurrentOffset { get; private set; } = RigidPose.Identity;
    public RigidPose ReferenceSpaceOffset => expectedStanding;
    public string InputDiagnostics => session.DescribeInput();

    public OpenVrFlightRuntime() : this(new OpenVrSession()) { }
    public void OpenBindings() => session.OpenBindings();

    public OpenVrFlightRuntime(IOpenVrSession session)
    {
        this.session = session;
        try
        {
            originalStanding = expectedStanding = session.ReadWorkingStanding();
            if (!originalStanding.IsValid) throw new InvalidOperationException("Standing 原点が不正です。");
        }
        catch { session.Dispose(); throw; }
    }

    private void VerifyUnchanged()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!session.ReadWorkingStanding().NearlyEquals(expectedStanding))
            throw new InvalidOperationException("SteamVR の基準空間が他のツールに変更されたため停止しました。空間操作ツールを停止して再接続してください。");
    }

    public InputFrame ReadPhysical()
    {
        VerifyUnchanged();
        var raw = session.ReadRaw();
        var rawToPhysical = originalStanding.Inverse();
        return new(rawToPhysical * raw.Head, raw.HeadTracked,
            raw.Left with { Pose = rawToPhysical * raw.Left.Pose },
            raw.Right with { Pose = rawToPhysical * raw.Right.Pose });
    }

    public void Apply(RigidPose offset)
    {
        if (!offset.IsValid) throw new ArgumentException("空間変換が不正です。", nameof(offset));
        VerifyUnchanged();
        // P = S0^-1 * raw; desired game pose = D * P.
        // Therefore S = S0 * D^-1 and S^-1 * raw = D * P.
        var target = originalStanding * offset.Inverse();
        session.PreviewStanding(target);
        expectedStanding = session.ReadWorkingStanding();
        previewOwned = true;
        if (!expectedStanding.NearlyEquals(target, 0.000001f))
        {
            Restore();
            throw new NotSupportedException("SteamVRのChaperoneは要求した回転を保持できません。XYZ回転にはFlugelKranzドライバーが必要です。");
        }
        CurrentOffset = offset;
    }

    public void Restore()
    {
        VerifyUnchanged();
        if (previewOwned)
        {
            session.PreviewStanding(originalStanding);
            expectedStanding = originalStanding;
            session.HidePreview();
            previewOwned = false;
        }
        CurrentOffset = OriginalOffset;
    }

    public void Dispose()
    {
        if (disposed) return;
        try { if (previewOwned) Restore(); }
        finally { disposed = true; session.Dispose(); }
    }
}
