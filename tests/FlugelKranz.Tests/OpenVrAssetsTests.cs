using System.Text.Json;
using FlugelKranz.OpenVR;
using Xunit;

namespace FlugelKranz.Tests;

public sealed class OpenVrAssetsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FlugelKranz-assets-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BundledManifestsPointToActualExecutableAndExistingBindings()
    {
        var executable = Path.Combine(root, "app with spaces", "renamed.exe");
        var paths = OpenVrAssets.Prepare(Path.GetDirectoryName(executable)!, executable, Path.Combine(root, "cache"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(paths.ApplicationManifest));
        var application = manifest.RootElement.GetProperty("applications")[0];
        Assert.Equal(executable, application.GetProperty("binary_path_windows").GetString());
        Assert.Equal(paths.ActionManifest, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(paths.ApplicationManifest)!,
            application.GetProperty("action_manifest_path").GetString()!)));
        using var actions = JsonDocument.Parse(File.ReadAllText(paths.ActionManifest));
        foreach (var binding in actions.RootElement.GetProperty("default_bindings").EnumerateArray())
        {
            var path = Path.Combine(Path.GetDirectoryName(paths.ActionManifest)!, binding.GetProperty("binding_url").GetString()!);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    [Fact]
    public void MovingExecutableDoesNotRewritePreviousLaunchManifest()
    {
        var cache = Path.Combine(root, "cache");
        var first = OpenVrAssets.Prepare(root, Path.Combine(root, "first.exe"), cache);
        var content = File.ReadAllText(first.ApplicationManifest);
        var second = OpenVrAssets.Prepare(root, Path.Combine(root, "second.exe"), cache);
        Assert.NotEqual(first.ApplicationManifest, second.ApplicationManifest);
        Assert.Equal(content, File.ReadAllText(first.ApplicationManifest));
    }

    [Fact]
    public void ExistingFolderDeploymentKeepsUserManifests()
    {
        Directory.CreateDirectory(Path.Combine(root, "Actions"));
        var manifest = Path.Combine(root, "manifest.vrmanifest");
        var actions = Path.Combine(root, "Actions", "actions.json");
        File.WriteAllText(manifest, "custom manifest");
        File.WriteAllText(actions, "custom actions");
        var paths = OpenVrAssets.Prepare(root, Path.Combine(root, "app.exe"), Path.Combine(root, "cache"));
        Assert.Equal(new OpenVrAssets.Paths(manifest, actions), paths);
        Assert.Equal("custom manifest", File.ReadAllText(manifest));
        Assert.False(Directory.Exists(Path.Combine(root, "cache")));
    }

    [Fact]
    public void RepeatedPreparationReusesValidFilesAndRepairsMissingBinding()
    {
        var cache = Path.Combine(root, "cache");
        var executable = Path.Combine(root, "app.exe");
        var first = OpenVrAssets.Prepare(root, executable, cache);
        var sentinelTime = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first.ActionManifest, sentinelTime);
        var binding = Path.Combine(Path.GetDirectoryName(first.ActionManifest)!, "bindings_touch.json");
        File.Delete(binding);
        var second = OpenVrAssets.Prepare(root, executable, cache);
        Assert.Equal(first, second);
        Assert.Equal(sentinelTime, File.GetLastWriteTimeUtc(second.ActionManifest));
        Assert.True(File.Exists(binding));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
