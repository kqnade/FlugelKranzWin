using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FlugelKranz.OpenVR;

/// <summary>Materializes SteamVR's file-based input manifests for a bundled application.</summary>
public static class OpenVrAssets
{
    public sealed record Paths(string ApplicationManifest, string ActionManifest);

    public static Paths Resolve() => Prepare(AppContext.BaseDirectory,
        Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable."),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlugelKranz", "assets"));

    public static Paths Prepare(string applicationDirectory, string executablePath, string cacheDirectory)
    {
        var manifest = Path.Combine(applicationDirectory, "manifest.vrmanifest");
        var actions = Path.Combine(applicationDirectory, "Actions", "actions.json");
        if (File.Exists(manifest) && File.Exists(actions))
            return new(manifest, actions);

        executablePath = Path.GetFullPath(executablePath);
        var assembly = typeof(OpenVrAssets).Assembly;
        // Different executable locations must not overwrite each other's launch manifest.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            executablePath + "|" + assembly.ManifestModule.ModuleVersionId)));
        var directory = Path.Combine(cacheDirectory, key);
        Directory.CreateDirectory(Path.Combine(directory, "Actions"));
        foreach (var name in new[] { "actions.json", "bindings_touch.json", "bindings_knuckles.json" })
            WriteIfChanged(Path.Combine(directory, "Actions", name), ReadResource(assembly, "Actions/" + name));

        var document = JsonNode.Parse(ReadResource(assembly, "manifest.vrmanifest"))!;
        document["applications"]![0]!["binary_path_windows"] = executablePath;
        manifest = Path.Combine(directory, "manifest.vrmanifest");
        WriteIfChanged(manifest, Encoding.UTF8.GetBytes(document.ToJsonString()));
        return new(manifest, Path.Combine(directory, "Actions", "actions.json"));
    }

    private static byte[] ReadResource(Assembly assembly, string name)
    {
        using var source = assembly.GetManifestResourceStream("OpenVrAssets/" + name)
            ?? throw new InvalidOperationException($"Missing bundled SteamVR asset: {name}");
        using var output = new MemoryStream();
        source.CopyTo(output);
        return output.ToArray();
    }

    private static void WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
