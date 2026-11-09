using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlugelKranz;

internal interface IDriverRegistration
{
    IReadOnlyList<string> ReadDrivers();
    void Add(string path);
    void Remove(string path);
}

internal sealed record DriverSetupResult(string DriverPath, bool Changed);

internal static class DriverSetup
{
    private const string DriverRelativePath = "driver/flugelkranz";
    private const string SettingsRelativePath = "resources/settings/default.vrsettings";

    internal static string InstallationRoot(string localAppData, string pathsFile)
    {
        var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(pathsFile).ToUpperInvariant())));
        return Path.Combine(localAppData, "FlugelKranz", "packages", profile);
    }

    internal static DriverSetupResult Ensure(string installRoot, string driverHash, Action<string> extract,
        IDriverRegistration registration, Func<bool> steamVrRunning)
    {
        installRoot = Path.GetFullPath(installRoot);
        if (driverHash.Length != 64 || driverHash.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Invalid driver hash.", nameof(driverHash));
        var lockKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installRoot.ToUpperInvariant())));
        using var mutex = new Mutex(false, "Local\\FlugelKranz.Setup." + lockKey);
        bool locked;
        try { locked = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { locked = true; }
        if (!locked) throw new IOException("別のFlugelKranzがドライバーを準備しています。少し待って開き直してください。");
        try
        {
            var previous = registration.ReadDrivers().Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(IsFlugelKranz).ToArray();
            var current = previous.FirstOrDefault(path => MatchesDriver(path, driverHash));
            if (current is not null && previous.Length == 1) return new(current, false);

            RequireSteamVrStopped(steamVrRunning);
            if (current is null)
            {
                var packageRoot = Path.Combine(installRoot, driverHash);
                current = Normalize(Path.Combine(packageRoot, DriverRelativePath));
                if (!File.Exists(Path.Combine(packageRoot, ".complete")) || !IsFlugelKranz(current) || !MatchesDriver(current, driverHash))
                {
                    // An interrupted extraction is never repaired in place: its DLL might be in use.
                    if (Directory.Exists(packageRoot) || File.Exists(packageRoot))
                        packageRoot += "-" + Guid.NewGuid().ToString("N");
                    current = Normalize(Path.Combine(packageRoot, DriverRelativePath));
                    extract(packageRoot);
                    if (!IsFlugelKranz(current) || !MatchesDriver(current, driverHash))
                        throw new IOException("展開したドライバーの内容を確認できませんでした。登録は変更していません。");
                    File.WriteAllText(Path.Combine(packageRoot, ".complete"), driverHash);
                }
                RequireSteamVrStopped(steamVrRunning);
                // Also refresh a completed, unregistered candidate after a failed registration.
                var settings = previous.Select(path => Path.Combine(path, SettingsRelativePath)).FirstOrDefault(File.Exists);
                if (settings is not null)
                    File.Copy(settings, Path.Combine(current, SettingsRelativePath), overwrite: true);
            }

            RequireSteamVrStopped(steamVrRunning);
            // Add and verify first: a failed add must never remove the working registration.
            if (!registration.ReadDrivers().Any(path => SamePath(path, current))) registration.Add(current);
            if (!registration.ReadDrivers().Any(path => SamePath(path, current)))
                throw new IOException("SteamVRへのドライバー登録を確認できませんでした。もう一度開き直してください。");
            foreach (var old in previous.Where(path => !SamePath(path, current)))
            {
                RequireSteamVrStopped(steamVrRunning);
                registration.Remove(old);
            }
            if (registration.ReadDrivers().Any(path => previous.Any(old => SamePath(path, old)) && !SamePath(path, current)))
                throw new IOException("古いFlugelKranzの登録が残っています。SteamVRを停止したまま、アプリを開き直してください。");
            return new(current, true);
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static void RequireSteamVrStopped(Func<bool> steamVrRunning)
    {
        if (steamVrRunning())
            throw new InvalidOperationException("ドライバーの自動準備が必要です。SteamVRを終了してからFlugelKranzを開き直してください。再起動やDLLの上書きは行っていません。");
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    private static bool MatchesDriver(string path, string hash)
    {
        var file = Path.Combine(path, "bin/win64/driver_flugelkranz.dll");
        if (!File.Exists(file) || !File.Exists(Path.Combine(path, SettingsRelativePath))) return false;
        using var stream = File.OpenRead(file);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), hash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFlugelKranz(string path)
    {
        var manifest = Path.Combine(path, "driver.vrdrivermanifest");
        if (!File.Exists(manifest)) return false;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String && name.GetString() == "flugelkranz";
        }
        catch (JsonException) { return false; } // An unrecognizable manifest is not ours to unregister.
    }
}
