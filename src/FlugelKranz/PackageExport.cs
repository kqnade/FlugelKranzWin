namespace FlugelKranz;

/// <summary>Exports bundled driver/support files without registering or loading the driver.</summary>
public static class PackageExport
{
    public static void Export(string destination)
    {
        var assembly = typeof(PackageExport).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Package/", StringComparison.Ordinal)).ToArray();
        if (!resources.Any(name => name.Replace('\\', '/') == "Package/driver/flugelkranz/bin/win64/driver_flugelkranz.dll"))
            throw new InvalidOperationException("このビルドにはドライバー一式が内蔵されていません。build-windows.ps1 -SingleFile で作成してください。");

        var root = Path.GetFullPath(destination);
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException("展開先が既に存在します。使用中のドライバーを保護するため、新しいフォルダーを指定してください。");
        Directory.CreateDirectory(root);
        foreach (var name in resources)
        {
            var relative = name["Package/".Length..].Replace('\\', '/');
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Invalid package entry: {relative}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing package entry: {name}");
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
    }
}
