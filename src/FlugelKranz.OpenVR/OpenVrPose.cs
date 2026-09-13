using System.Numerics;
using FlugelKranz.Core;
using Valve.VR;

namespace FlugelKranz.OpenVR;

public static class OpenVrPose
{
    public static RigidPose FromMatrix(HmdMatrix34_t m)
    {
        // OpenVR uses column vectors; System.Numerics uses row vectors.
        var matrix = new Matrix4x4(
            m.m0, m.m4, m.m8, 0, m.m1, m.m5, m.m9, 0,
            m.m2, m.m6, m.m10, 0, m.m3, m.m7, m.m11, 1);
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position)
            || Vector3.Distance(scale, Vector3.One) > 0.001f)
            throw new InvalidOperationException("SteamVR が不正な剛体変換を返しました。");
        var pose = new RigidPose(rotation, position);
        if (!pose.IsValid) throw new InvalidOperationException("SteamVR が不正な位置・姿勢を返しました。");
        return pose;
    }

    public static HmdMatrix34_t ToMatrix(RigidPose pose)
    {
        if (!pose.IsValid) throw new ArgumentException("空間変換が不正です。", nameof(pose));
        var m = Matrix4x4.CreateFromQuaternion(pose.Orientation);
        return new() {
            m0 = m.M11, m1 = m.M21, m2 = m.M31, m3 = pose.Position.X,
            m4 = m.M12, m5 = m.M22, m6 = m.M32, m7 = pose.Position.Y,
            m8 = m.M13, m9 = m.M23, m10 = m.M33, m11 = pose.Position.Z
        };
    }
}
