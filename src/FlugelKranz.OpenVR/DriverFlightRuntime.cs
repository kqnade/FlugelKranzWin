using FlugelKranz.Core;

namespace FlugelKranz.OpenVR;

public sealed class DriverFlightRuntime : IFlightRuntime, IReferenceSpaceOffsetProvider, IFlightBindings, IFlightInputControl, IFlightInputDiagnostics
{
    private readonly IOpenVrSession session;
    private readonly IDriverConnection driver;
    private readonly RigidPose standing;
    private bool disposed;
    public RigidPose OriginalOffset => RigidPose.Identity;
    public RigidPose CurrentOffset { get; private set; } = RigidPose.Identity;
    public RigidPose ReferenceSpaceOffset => CurrentOffset;
    public string InputDiagnostics => session.DescribeInput();

    public DriverFlightRuntime() : this(new OpenVrSession(), () => new DriverConnection()) { }
    public DriverFlightRuntime(IOpenVrSession session, Func<IDriverConnection> connect)
    {
        this.session = session;
        try
        {
            standing = session.ReadWorkingStanding();
            if (!standing.IsValid) throw new InvalidOperationException("Standing 原点が不正です。");
            driver = connect();
        }
        catch { session.Dispose(); throw; }
    }

    public InputFrame ReadPhysical()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        driver.Refresh();
        if (!session.ReadWorkingStanding().NearlyEquals(standing))
            throw new InvalidOperationException("SteamVRの基準空間が変更されました。飛行変換を解除して再接続してください。");
        var input = session.ReadRaw();
        var (left, right) = session.ControllerDevices();
        var rawToPhysical = standing.Inverse();
        var head = driver.Read(0);
        HandSample ReadHand(HandSample hand, uint device)
        {
            var raw = driver.Read(device);
            bool tracked = raw.Tracked && hand.IsTracked;
            return hand with { Pose = rawToPhysical * raw.Pose, IsTracked = tracked };
        }
        return input with
        {
            Head = rawToPhysical * head.Pose,
            HeadTracked = head.Tracked && input.HeadTracked,
            Left = ReadHand(input.Left, left), Right = ReadHand(input.Right, right)
        };
    }

    public void Apply(RigidPose offset)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!offset.IsValid) throw new ArgumentException("空間変換が不正です。", nameof(offset));
        // Game pose S^-1 * (S * D * S^-1) * raw = D * physical.
        driver.Apply(standing * offset * standing.Inverse());
        CurrentOffset = offset;
    }

    public void SetPilotInputEnabled(bool enabled) => session.SetPilotInputEnabled(enabled);
    public void Restore() => Apply(OriginalOffset);
    public void OpenBindings() => session.OpenBindings();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { driver.Dispose(); }
        finally { session.Dispose(); }
    }
}
