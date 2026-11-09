using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using FlugelKranz;
using Xunit;

namespace FlugelKranz.Tests;

public sealed class DriverSetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FlugelKranz-setup-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] payload = [1, 2, 3, 4];
    private readonly FakeRegistration registry = new();
    private int extractions;
    private string Hash => Convert.ToHexString(SHA256.HashData(payload));
    private string InstallRoot => Path.Combine(root, "packages");

    [Fact]
    public void FirstLaunchExtractsRegistersAndSecondLaunchIsUnchanged()
    {
        var first = Ensure();
        Assert.True(first.Changed);
        Assert.Equal([first.DriverPath], registry.Paths);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(first.DriverPath, "bin/win64/driver_flugelkranz.dll")));
        var second = Ensure(running: true);
        Assert.False(second.Changed);
        Assert.Equal(first.DriverPath, second.DriverPath);
        Assert.Equal(1, extractions);
        Assert.Single(registry.Operations);
    }

    [Fact]
    public void CurrentManualInstallationIsReusedWithoutWritingFiles()
    {
        var existing = CreateDriver("manual", payload);
        registry.Paths.Add(existing);
        var result = Ensure(running: true);
        Assert.Equal(existing, result.DriverPath);
        Assert.False(result.Changed);
        Assert.Equal(0, extractions);
        Assert.Empty(registry.Operations);
        Assert.False(Directory.Exists(InstallRoot));
    }

    [Fact]
    public void UpdatePreservesSettingsAndOldDllAndLeavesOtherDriversRegistered()
    {
        var old = CreateDriver("old", [9]);
        var unrelated = CreateDriver("unrelated", [8], "other_driver");
        registry.Paths.AddRange([old, unrelated]);
        var oldSettings = Path.Combine(old, "resources/settings/default.vrsettings");
        File.WriteAllText(oldSettings, "{\"driver_flugelkranz\":{\"observeFrames\":false}}");

        var result = Ensure();

        Assert.True(result.Changed);
        Assert.Equal(["add:" + result.DriverPath, "remove:" + old], registry.Operations);
        Assert.Equal([unrelated, result.DriverPath], registry.Paths);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(old, "bin/win64/driver_flugelkranz.dll")));
        Assert.Equal(File.ReadAllText(oldSettings), File.ReadAllText(Path.Combine(result.DriverPath, "resources/settings/default.vrsettings")));
    }

    [Fact]
    public void RunningSteamVrDefersAnUpdateWithoutExtractionOrRegistration()
    {
        registry.Paths.Add(CreateDriver("old", [9]));
        Assert.Throws<InvalidOperationException>(() => Ensure(running: true));
        Assert.Equal(0, extractions);
        Assert.Empty(registry.Operations);
    }

    [Fact]
    public void SteamVrStartingDuringExtractionDefersRegistration()
    {
        bool running = false;
        Assert.Throws<InvalidOperationException>(() => DriverSetup.Ensure(InstallRoot, Hash, path =>
        {
            Extract(path);
            running = true;
        }, registry, () => running));
        Assert.Empty(registry.Operations);
    }

    [Fact]
    public void FailedExtractionKeepsOldRegistrationAndRetryUsesFreshDirectory()
    {
        var old = CreateDriver("old", [9]);
        registry.Paths.Add(old);
        Assert.Throws<IOException>(() => DriverSetup.Ensure(InstallRoot, Hash, path =>
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "partial"), "keep");
            throw new IOException("disk full");
        }, registry, () => false));
        Assert.Equal([old], registry.Paths);
        Assert.Empty(registry.Operations);
        var result = Ensure();
        Assert.True(result.Changed);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(InstallRoot, Hash, "partial")));
        Assert.Contains(Hash + "-", result.DriverPath);
    }

    [Fact]
    public void IncorrectExtractedBinaryIsNeverRegistered()
    {
        Assert.Throws<IOException>(() => DriverSetup.Ensure(InstallRoot, Hash,
            path => WriteDriver(Path.Combine(path, "driver/flugelkranz"), [99]), registry, () => false));
        Assert.Empty(registry.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrIneffectiveRegistrationNeverRemovesOldDriver(bool ineffective)
    {
        var old = CreateDriver("old", [9]);
        registry.Paths.Add(old);
        registry.FailAdd = !ineffective;
        registry.IgnoreAdd = ineffective;
        Assert.Throws<IOException>(() => Ensure());
        Assert.Equal([old], registry.Paths);
        Assert.DoesNotContain(registry.Operations, operation => operation.StartsWith("remove:"));
        var settings = Path.Combine(old, "resources/settings/default.vrsettings");
        File.WriteAllText(settings, "{\"latest\":true}");
        registry.FailAdd = registry.IgnoreAdd = false;
        var result = Ensure();
        Assert.True(result.Changed);
        Assert.Equal(File.ReadAllText(settings), File.ReadAllText(Path.Combine(result.DriverPath, "resources/settings/default.vrsettings")));
        Assert.Equal(1, extractions); // Completed extraction is reused after registration failure.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrIneffectiveRemovalIsReportedAndRetried(bool ineffective)
    {
        var old = CreateDriver("old", [9]);
        registry.Paths.Add(old);
        registry.FailRemove = !ineffective;
        registry.IgnoreRemove = ineffective;
        Assert.Throws<IOException>(() => Ensure());
        Assert.Contains(old, registry.Paths);
        registry.FailRemove = registry.IgnoreRemove = false;
        Assert.True(Ensure().Changed);
        Assert.DoesNotContain(old, registry.Paths);
        Assert.Equal(1, extractions);
    }

    [Fact]
    public void MissingSteamVrRegistrationFileIsAnActionableError()
    {
        var registration = new SteamVrDriverRegistration(Path.Combine(root, "missing.vrpath"));
        Assert.Contains("SteamVRを一度起動", Assert.Throws<IOException>(() => registration.ReadDrivers()).Message);
    }

    [Theory]
    [InlineData("{\"runtime\":[\"C:/SteamVR\"]}")]
    [InlineData("{\"runtime\":[\"C:/SteamVR\"],\"external_drivers\":null}")]
    public void UnregisteredSteamVrReadsAsEmpty(string json)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "paths.vrpath");
        File.WriteAllText(path, json);
        Assert.Empty(new SteamVrDriverRegistration(path).ReadDrivers());
    }

    [Theory]
    [InlineData(true, "adddriver")]
    [InlineData(false, "removedriver")]
    public void RegistrationAdapterPassesExactOperationPathAndRegistryToTool(bool add, string operation)
    {
        var (pathsFile, tool) = CreateRegistryFixture();
        ProcessStartInfo? invocation = null;
        var registration = new SteamVrDriverRegistration(pathsFile, start =>
        {
            Assert.Null(invocation);
            invocation = start;
            return (0, "ok");
        });
        var driverPath = Path.Combine(root, "driver with spaces", "flugelkranz");
        if (add) registration.Add(driverPath);
        else registration.Remove(driverPath);
        Assert.NotNull(invocation);
        Assert.Equal(tool, invocation.FileName);
        Assert.Equal([operation, driverPath], invocation.ArgumentList);
        Assert.Equal(pathsFile, invocation.Environment["VR_PATHREG_OVERRIDE"]);
        Assert.False(invocation.UseShellExecute);
        Assert.True(invocation.CreateNoWindow);
        Assert.True(invocation.RedirectStandardError && invocation.RedirectStandardOutput);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegistrationAdapterReportsToolFailure(bool add)
    {
        var (pathsFile, _) = CreateRegistryFixture();
        var registration = new SteamVrDriverRegistration(pathsFile, _ => (42, "access denied"));
        var exception = Assert.Throws<IOException>(() =>
        {
            if (add) registration.Add(Path.Combine(root, "driver"));
            else registration.Remove(Path.Combine(root, "driver"));
        });
        Assert.Contains("42", exception.Message);
        Assert.Contains("access denied", exception.Message);
    }

    [Fact]
    public void MissingToolDoesNotAttemptARegistrationProcess()
    {
        var (pathsFile, tool) = CreateRegistryFixture();
        File.Delete(tool);
        bool called = false;
        var registration = new SteamVrDriverRegistration(pathsFile, _ => { called = true; return (0, ""); });
        Assert.Throws<IOException>(() => registration.Add(Path.Combine(root, "driver")));
        Assert.False(called);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("custom profile/paths.vrpath")]
    public void RegistryOverrideIsResolvedConsistently(string? overridePath)
    {
        var expected = string.IsNullOrEmpty(overridePath) ? Path.Combine(root, "openvr", "openvrpaths.vrpath") : Path.GetFullPath(overridePath);
        Assert.Equal(expected, SteamVrDriverRegistration.ResolvePathsFile(root, overridePath));
    }

    private (string PathsFile, string Tool) CreateRegistryFixture()
    {
        var runtime = Path.Combine(root, "Steam VR runtime");
        var tool = Path.Combine(runtime, "bin/win64/vrpathreg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        File.WriteAllBytes(tool, []); // Never executed: the process boundary is injected above.
        var pathsFile = Path.Combine(root, "overridden paths.vrpath");
        File.WriteAllText(pathsFile, JsonSerializer.Serialize(new { runtime = new[] { runtime }, external_drivers = Array.Empty<string>() }));
        return (pathsFile, tool);
    }

    [Fact]
    public void SeparateRegistriesCannotOverwriteEachOthersPreparedSettings()
    {
        var oldA = CreateDriver("oldA", [9]);
        var oldB = CreateDriver("oldB", [8]);
        File.WriteAllText(Path.Combine(oldA, "resources/settings/default.vrsettings"), "{\"profile\":1}");
        File.WriteAllText(Path.Combine(oldB, "resources/settings/default.vrsettings"), "{\"profile\":2}");
        registry.Paths.Add(oldA);
        var registryB = new FakeRegistration();
        registryB.Paths.Add(oldB);
        var rootA = DriverSetup.InstallationRoot(root, Path.Combine(root, "a.vrpath"));
        var rootB = DriverSetup.InstallationRoot(root, Path.Combine(root, "b.vrpath"));
        var first = DriverSetup.Ensure(rootA, Hash, Extract, registry, () => false);
        var second = DriverSetup.Ensure(rootB, Hash, Extract, registryB, () => false);
        Assert.NotEqual(first.DriverPath, second.DriverPath);
        Assert.Equal("{\"profile\":1}", File.ReadAllText(Path.Combine(first.DriverPath, "resources/settings/default.vrsettings")));
        Assert.Equal("{\"profile\":2}", File.ReadAllText(Path.Combine(second.DriverPath, "resources/settings/default.vrsettings")));
    }

    private DriverSetupResult Ensure(bool running = false) => DriverSetup.Ensure(InstallRoot, Hash, Extract, registry, () => running);
    private void Extract(string destination)
    {
        extractions++;
        Assert.False(Directory.Exists(destination));
        WriteDriver(Path.Combine(destination, "driver/flugelkranz"), payload);
    }
    private string CreateDriver(string name, byte[] bytes, string driverName = "flugelkranz")
    {
        var directory = Path.Combine(root, name);
        WriteDriver(directory, bytes, driverName);
        return directory;
    }
    private static void WriteDriver(string directory, byte[] bytes, string driverName = "flugelkranz")
    {
        Directory.CreateDirectory(Path.Combine(directory, "bin/win64"));
        Directory.CreateDirectory(Path.Combine(directory, "resources/settings"));
        File.WriteAllBytes(Path.Combine(directory, "bin/win64/driver_flugelkranz.dll"), bytes);
        File.WriteAllText(Path.Combine(directory, "driver.vrdrivermanifest"), "{\"name\":\"" + driverName + "\"}");
        File.WriteAllText(Path.Combine(directory, "resources/settings/default.vrsettings"), "{}");
    }
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class FakeRegistration : IDriverRegistration
    {
        internal List<string> Paths { get; } = [];
        internal List<string> Operations { get; } = [];
        internal bool FailAdd, IgnoreAdd, FailRemove, IgnoreRemove;
        public IReadOnlyList<string> ReadDrivers() => Paths.ToArray();
        public void Add(string path)
        {
            Operations.Add("add:" + path);
            if (FailAdd) throw new IOException("add failed");
            if (!IgnoreAdd) Paths.Add(path);
        }
        public void Remove(string path)
        {
            Operations.Add("remove:" + path);
            if (FailRemove) throw new IOException("remove failed");
            if (!IgnoreRemove) Paths.Remove(path);
        }
    }
}
