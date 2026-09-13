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
}

public sealed class OpenVrFlightRuntime : IFlightRuntime, IReferenceSpaceOffsetProvider
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

    public OpenVrFlightRuntime(Func<ValveIndexInputSettings>? settings = null)
        : this(new OpenVrSession(settings)) { }

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
        expectedStanding = target;
        previewOwned = true;
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
