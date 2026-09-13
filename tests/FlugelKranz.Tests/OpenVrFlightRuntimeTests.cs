using System.Numerics;
using FlugelKranz.Core;
using FlugelKranz.OpenVR;
using Valve.VR;
using Xunit;

namespace FlugelKranz.Tests;

public class OpenVrFlightRuntimeTests
{
    private static RigidPose Pose(float yaw, float pitch, float roll, Vector3 position) =>
        new(Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll), position);

    [Fact]
    public void MatrixRoundTripPreservesAllRotationAxesAndTranslation()
    {
        var pose = Pose(0.7f, -0.6f, 1.2f, new(3, -4, 5));
        Assert.True(OpenVrPose.FromMatrix(OpenVrPose.ToMatrix(pose)).NearlyEquals(pose));
    }

    [Fact]
    public void OpenVrColumnMatrixTurnsForwardTowardNegativeX()
    {
        var matrix = new HmdMatrix34_t { m2 = 1, m5 = 1, m8 = -1, m3 = 3, m7 = 2, m11 = 1 };
        Assert.True(Vector3.Distance(OpenVrPose.FromMatrix(matrix).Transform(-Vector3.UnitZ), new(2, 2, 1)) < 0.0001f);
    }

    [Fact]
    public void MatrixRejectsNonRigidScale() =>
        Assert.Throws<InvalidOperationException>(() => OpenVrPose.FromMatrix(new() { m0 = 2, m5 = 1, m10 = 1 }));

    [Fact]
    public void MatrixRejectsInvalidPose() =>
        Assert.Throws<ArgumentException>(() => OpenVrPose.ToMatrix(default));

    [Fact]
    public void ApplyTransformsGamePoseInPhysicalCoordinatesWithNonIdentityOrigin()
    {
        var session = new FakeSession { Standing = Pose(0.8f, 0, 0, new(4, 2, -3)) };
        var original = session.Standing;
        using var runtime = new OpenVrFlightRuntime(session);
        var flight = Pose(-0.4f, 0.5f, -0.7f, new(-2, 1, 3));
        var physicalHead = Pose(0.1f, 0.2f, 0.3f, new(0.3f, 1.7f, -0.5f));
        runtime.Apply(flight);
        var gameHead = session.Standing.Inverse() * original * physicalHead;
        Assert.True(gameHead.NearlyEquals(flight * physicalHead));
    }

    [Fact]
    public void RawInputDoesNotFeedAppliedFlightBackIntoMotion()
    {
        var session = new FakeSession { Standing = Pose(1, 0, 0, new(2, 0, 3)) };
        var physicalHead = Pose(0.2f, 0.3f, 0, new(1, 1.7f, 0));
        session.Frame = new(session.Standing * physicalHead, true, default, default);
        using var runtime = new OpenVrFlightRuntime(session);
        runtime.Apply(Pose(-1, 0.7f, 0.8f, new(5, -4, 3)));
        Assert.True(runtime.ReadPhysical().Head.NearlyEquals(physicalHead));
    }

    [Fact]
    public void DiagnoseReadAndDisposeDoNotWriteSpace()
    {
        var session = new FakeSession();
        using (var runtime = new OpenVrFlightRuntime(session)) runtime.ReadPhysical();
        Assert.Equal(0, session.Writes);
    }

    [Fact]
    public void RestoreReturnsOriginalStanding()
    {
        var session = new FakeSession { Standing = Pose(0.4f, 0, 0, new(3, 0, 2)) };
        var original = session.Standing;
        using var runtime = new OpenVrFlightRuntime(session);
        runtime.Apply(Pose(0.3f, 0.4f, 0.5f, new(2, 3, 4)));
        runtime.Restore();
        Assert.True(session.Standing.NearlyEquals(original));
    }

    [Fact]
    public void RestoreHidesOwnedPreviewOnlyOnce()
    {
        var session = new FakeSession();
        using var runtime = new OpenVrFlightRuntime(session);
        runtime.Apply(Pose(0, 0.2f, 0, new(1, 0, 0)));
        runtime.Restore();
        runtime.Restore();
        Assert.Equal(1, session.Hides);
    }

    [Fact]
    public void DisposeRestoresActivePreview()
    {
        var session = new FakeSession();
        var runtime = new OpenVrFlightRuntime(session);
        runtime.Apply(Pose(0.4f, 0.3f, 0.2f, new(4, 3, 2)));
        runtime.Dispose();
        Assert.True(session.Standing.NearlyEquals(RigidPose.Identity));
    }

    [Fact]
    public void ExternalChangeIsNotOverwrittenDuringRestoreOrDispose()
    {
        var session = new FakeSession();
        var runtime = new OpenVrFlightRuntime(session);
        runtime.Apply(Pose(0.1f, 0, 0, new(1, 0, 0)));
        session.Standing = Pose(0.8f, 0, 0, new(10, 0, 0));
        Assert.Throws<InvalidOperationException>(() => runtime.Restore());
        Assert.Throws<InvalidOperationException>(() => runtime.Dispose());
        Assert.Equal(1, session.Writes);
        Assert.True(session.Disposed);
    }

    [Fact]
    public void ExternalChangePreventsNextApply()
    {
        var session = new FakeSession();
        using var runtime = new OpenVrFlightRuntime(session);
        session.Standing = Pose(0, 0, 0, new(1, 0, 0));
        Assert.Throws<InvalidOperationException>(() => runtime.Apply(RigidPose.Identity));
        Assert.Equal(0, session.Writes);
    }

    [Fact]
    public void FailedWriteDoesNotAdvanceCurrentOffset()
    {
        var session = new FakeSession { FailWrites = true };
        using var runtime = new OpenVrFlightRuntime(session);
        Assert.Throws<IOException>(() => runtime.Apply(Pose(0.5f, 0, 0, new(1, 0, 0))));
        Assert.Equal(RigidPose.Identity, runtime.CurrentOffset);
    }

    [Fact]
    public void InvalidOffsetNeverReachesNativeSession()
    {
        var session = new FakeSession();
        using var runtime = new OpenVrFlightRuntime(session);
        Assert.Throws<ArgumentException>(() => runtime.Apply(default));
        Assert.Equal(0, session.Writes);
    }

    [Fact]
    public void FailedConstructionDisposesNativeSession()
    {
        var session = new FakeSession { Standing = default };
        Assert.Throws<InvalidOperationException>(() => new OpenVrFlightRuntime(session));
        Assert.True(session.Disposed);
    }

    [Fact]
    public void RejectedPitchReportsUnsupportedAndRestoresWithoutExternalChangeError()
    {
        var session = new FakeSession { FlattenRotation = true };
        using var runtime = new OpenVrFlightRuntime(session);
        var error = Assert.Throws<NotSupportedException>(() => runtime.Apply(Pose(0, 0.2f, 0, Vector3.Zero)));
        Assert.Contains("ドライバー", error.Message);
        Assert.Equal(RigidPose.Identity, runtime.CurrentOffset);
        Assert.Equal(1, session.Hides);
    }

    private sealed class FakeSession : IOpenVrSession
    {
        public RigidPose Standing = RigidPose.Identity;
        public InputFrame Frame = new(RigidPose.Identity, true, default, default);
        public int Writes, Hides;
        public bool Disposed, FailWrites, FlattenRotation;
        public RigidPose ReadWorkingStanding() => Standing;
        public InputFrame ReadRaw() => Frame;
        public void PreviewStanding(RigidPose pose)
        {
            if (FailWrites) throw new IOException("write failed");
            Standing = FlattenRotation ? pose with { Orientation = Quaternion.Identity } : pose;
            Writes++;
        }
        public void HidePreview() => Hides++;
        public void Dispose() => Disposed = true;
    }
}
