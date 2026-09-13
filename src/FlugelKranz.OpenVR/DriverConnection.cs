using System.IO.MemoryMappedFiles;
using System.Numerics;
using FlugelKranz.Core;

namespace FlugelKranz.OpenVR;

public readonly record struct DriverSample(RigidPose Pose, bool Tracked);

public interface IDriverConnection : IDisposable
{
    DriverSample Read(uint device);
    void Apply(RigidPose rawTransform);
    void Refresh();
}

internal sealed class DriverConnection : IDriverConnection
{
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly Mutex mutex;
    private readonly uint owner = (uint)Environment.ProcessId;
    private bool disposed;

    public DriverConnection()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SteamVRドライバーIPCはWindows専用です。");
        try
        {
            mutex = Mutex.OpenExisting("Local\\FlugelKranz.Driver.Mutex.v1");
            try
            {
                mapping = MemoryMappedFile.OpenExisting("Local\\FlugelKranz.Driver.State.v1", MemoryMappedFileRights.ReadWrite);
                try { view = mapping.CreateViewAccessor(0, 4704, MemoryMappedFileAccess.ReadWrite); }
                catch { mapping.Dispose(); throw; }
            }
            catch { mutex.Dispose(); throw; }
        }
        catch (Exception e) when (e is FileNotFoundException or WaitHandleCannotBeOpenedException)
        {
            throw new InvalidOperationException("3軸回転用のFlugelKranzドライバーが起動していません。install-driver.ps1で登録し、SteamVRを再起動してください。", e);
        }
        try
        {
            Locked(() =>
            {
                VerifyDriver();
                long now = Environment.TickCount64;
                if (view.ReadUInt32(24) != 0 && now - view.ReadInt64(16) < 500)
                    throw new InvalidOperationException("別のFlugelKranzがドライバーを使用しています。");
                WritePose(32, RigidPose.Identity);
                view.Write(24, owner);
                view.Write(28, 1u);
                view.Write(16, now);
            });
        }
        catch { view.Dispose(); mapping.Dispose(); mutex.Dispose(); throw; }
    }

    private void VerifyDriver()
    {
        if (view.ReadUInt32(0) != 0x464b4452 || view.ReadUInt32(4) != 1 || view.ReadUInt32(88) != 64)
            throw new InvalidOperationException("FlugelKranzドライバーのIPCバージョンが一致しません。");
        long age = Environment.TickCount64 - view.ReadInt64(8);
        if (age is < 0 or > 1000) throw new InvalidOperationException("FlugelKranzドライバーの応答が停止しました。");
    }

    private void VerifyOwner()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        VerifyDriver();
        if (view.ReadUInt32(24) != owner) throw new InvalidOperationException("ドライバー接続の所有権が変更されました。");
    }

    public void Refresh() => Locked(() => { VerifyOwner(); view.Write(16, Environment.TickCount64); });

    public DriverSample Read(uint device)
    {
        DriverSample result = new(RigidPose.Identity, false);
        if (device >= 64) return result;
        Locked(() =>
        {
            VerifyOwner();
            long offset = 96 + device * 72;
            long age = Environment.TickCount64 - view.ReadInt64(offset);
            if (age is < 0 or > 100 || (view.ReadUInt32(offset + 8) & 3) != 3) return;
            var pose = ReadPose(offset + 16);
            if (pose.IsValid) result = new(pose, true);
        });
        return result;
    }

    public void Apply(RigidPose rawTransform)
    {
        if (!rawTransform.IsValid) throw new ArgumentException("ドライバーへ送る変換が不正です。", nameof(rawTransform));
        Locked(() =>
        {
            VerifyOwner();
            WritePose(32, rawTransform);
            view.Write(16, Environment.TickCount64);
            view.Write(28, 1u);
        });
    }

    private RigidPose ReadPose(long at) => new(
        new Quaternion((float)view.ReadDouble(at), (float)view.ReadDouble(at + 8),
            (float)view.ReadDouble(at + 16), (float)view.ReadDouble(at + 24)),
        new Vector3((float)view.ReadDouble(at + 32), (float)view.ReadDouble(at + 40), (float)view.ReadDouble(at + 48)));

    private void WritePose(long at, RigidPose pose)
    {
        view.Write(at, (double)pose.Orientation.X); view.Write(at + 8, (double)pose.Orientation.Y);
        view.Write(at + 16, (double)pose.Orientation.Z); view.Write(at + 24, (double)pose.Orientation.W);
        view.Write(at + 32, (double)pose.Position.X); view.Write(at + 40, (double)pose.Position.Y);
        view.Write(at + 48, (double)pose.Position.Z);
    }

    private void Locked(Action action)
    {
        bool acquired;
        try { acquired = mutex.WaitOne(100); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new TimeoutException("ドライバーの共有データが使用中です。");
        try { action(); }
        finally { mutex.ReleaseMutex(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        try
        {
            Locked(() =>
            {
                if (view.ReadUInt32(24) != owner) return;
                WritePose(32, RigidPose.Identity);
                view.Write(28, 0u); view.Write(24, 0u); view.Write(16, 0L);
            });
        }
        finally { disposed = true; view.Dispose(); mapping.Dispose(); mutex.Dispose(); }
    }
}
