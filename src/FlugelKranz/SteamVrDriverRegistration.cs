using System.Diagnostics;
using System.Text.Json;

namespace FlugelKranz;

internal sealed class SteamVrDriverRegistration : IDriverRegistration
{
    private readonly string pathsFile;
    private readonly Func<ProcessStartInfo, (int ExitCode, string Details)> execute;

    internal SteamVrDriverRegistration(string pathsFile, Func<ProcessStartInfo, (int ExitCode, string Details)>? execute = null)
    {
        this.pathsFile = Path.GetFullPath(pathsFile);
        this.execute = execute ?? Execute;
    }

    internal static string ResolvePathsFile(string localAppData, string? overridePath) => Path.GetFullPath(
        string.IsNullOrEmpty(overridePath) ? Path.Combine(localAppData, "openvr", "openvrpaths.vrpath") : overridePath);

    public IReadOnlyList<string> ReadDrivers()
    {
        using var json = ReadPaths();
        return json.RootElement.TryGetProperty("external_drivers", out var paths) && paths.ValueKind != JsonValueKind.Null
            ? paths.EnumerateArray().Select(path => path.GetString()
                ?? throw new IOException("SteamVRのドライバー登録パスが不正です。")).ToArray()
            : [];
    }

    public void Add(string path) => Run("adddriver", path);
    public void Remove(string path) => Run("removedriver", path);

    private JsonDocument ReadPaths()
    {
        if (!File.Exists(pathsFile))
            throw new IOException("SteamVRのインストール先が見つかりません。SteamVRを一度起動して終了してから、FlugelKranzを開き直してください。");
        return JsonDocument.Parse(File.ReadAllText(pathsFile));
    }

    private void Run(string operation, string path)
    {
        using var json = ReadPaths();
        var runtime = json.RootElement.GetProperty("runtime").EnumerateArray().Select(value => value.GetString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? throw new IOException("SteamVRのインストール先が登録されていません。");
        var tool = Path.Combine(runtime, "bin/win64/vrpathreg.exe");
        if (!File.Exists(tool)) throw new IOException("SteamVRの登録ツールが見つかりません。SteamVRのインストールを確認してください。");
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add(Path.GetFullPath(path));
        // Read and write the same registry, including installations using VR_PATHREG_OVERRIDE.
        start.Environment["VR_PATHREG_OVERRIDE"] = pathsFile;
        var result = execute(start);
        if (result.ExitCode != 0)
            throw new IOException($"SteamVRのドライバー登録に失敗しました ({result.ExitCode}): {result.Details}");
    }

    private static (int ExitCode, string Details) Execute(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new IOException("SteamVRの登録ツールを起動できませんでした。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new IOException("SteamVRの登録処理が時間内に完了しませんでした。アプリを開き直してください。");
        }
        var details = (output.GetAwaiter().GetResult() + " " + error.GetAwaiter().GetResult()).Trim();
        return (process.ExitCode, details);
    }
}
