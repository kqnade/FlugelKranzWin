param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$ExpectedDriver,
    [Parameter(Mandatory = $true)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$ExpectedDriver = (Resolve-Path -LiteralPath $ExpectedDriver).Path
$WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $WorkDirectory) { throw 'Use a new verification directory.' }
$isolated = Join-Path $WorkDirectory 'isolated app'
New-Item -ItemType Directory -Path $isolated | Out-Null
$app = Join-Path $isolated 'Renamed FlugelKranz.exe'
Copy-Item -LiteralPath $Executable -Destination $app
$previousCache = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
try {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $WorkDirectory 'runtime-cache'
    & $app --help | Out-File (Join-Path $WorkDirectory 'help.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated executable --help failed.' }
    if (@(Get-ChildItem -LiteralPath $isolated -File).Count -ne 1) { throw 'Unexpected sidecar file.' }

    $support = Join-Path $WorkDirectory 'support'
    & $app --extract-package $support
    if ($LASTEXITCODE -ne 0) { throw 'Package extraction failed.' }
    foreach ($relative in @('install-driver.ps1', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'LICENSE-OpenVR',
        'README.md', 'WINDOWS.md', 'native/driver/README.md', 'driver/flugelkranz/driver.vrdrivermanifest',
        'driver/flugelkranz/LICENSE-OpenVR', 'driver/flugelkranz/LICENSE-MinHook.txt',
        'driver/flugelkranz/resources/settings/default.vrsettings')) {
        if (!(Test-Path -LiteralPath (Join-Path $support $relative) -PathType Leaf)) { throw "Missing: $relative" }
    }
    $driver = Join-Path $support 'driver/flugelkranz/bin/win64/driver_flugelkranz.dll'
    if ((Get-FileHash -LiteralPath $driver).Hash -ne (Get-FileHash -LiteralPath $ExpectedDriver).Hash) {
        throw 'Bundled driver differs from the freshly built driver.'
    }
    # Deliberately alter a file: a second extraction must reject the directory, not overwrite it.
    $sentinel = Join-Path $support 'README.md'
    Set-Content -LiteralPath $sentinel -Value 'verification sentinel'
    & $app --extract-package $support 2>&1 | Out-File (Join-Path $WorkDirectory 'existing-directory.txt')
    if ($LASTEXITCODE -eq 0) { throw 'Existing destination was accepted.' }
    if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'verification sentinel') { throw 'Existing file was overwritten.' }

    $cacheFiles = @(Get-ChildItem -LiteralPath $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -Recurse -File)
    foreach ($nativeName in @('openvr_api.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll')) {
        if (!($cacheFiles | Where-Object Name -eq $nativeName)) { throw "Missing bundled native library: $nativeName" }
    }
    Write-Host "Single-file verification passed: $WorkDirectory"
} finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousCache
}
