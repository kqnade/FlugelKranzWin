param(
    [string]$Dotnet = 'dotnet',
    [string]$CMake = 'cmake',
    [switch]$SingleFile,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (!$OutputDirectory) {
        $OutputDirectory = if ($SingleFile) { 'artifacts/single-file/windows-x64' } else { 'artifacts/windows-x64' }
    }
    if (![IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot $OutputDirectory }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if ($SingleFile -and (Test-Path -LiteralPath $OutputDirectory)) {
        throw 'Single-file output already exists. Select a new -OutputDirectory to avoid overwriting a running application.'
    }
    & $CMake -S native/driver -B artifacts/driver-build -A x64
    if ($LASTEXITCODE -ne 0) { throw 'Driver configure failed' }
    & $CMake --build artifacts/driver-build --config Release
    if ($LASTEXITCODE -ne 0) { throw 'Driver build failed' }
    $publishArguments = @('publish', 'src/FlugelKranz', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $OutputDirectory)
    if (!$SingleFile) {
        & $Dotnet @publishArguments
        if ($LASTEXITCODE -ne 0) { throw 'Application publish failed' }
    }
    # Stage a fresh payload for the bundle; never write to a registered driver's location.
    $packagePath = if ($SingleFile) {
        Join-Path $PSScriptRoot ('artifacts/package-input/' + [Guid]::NewGuid().ToString('N'))
    } else { $OutputDirectory }
    $driverPath = Join-Path $packagePath 'driver/flugelkranz'
    New-Item -ItemType Directory -Force (Join-Path $driverPath 'bin/win64') | Out-Null
    Copy-Item -LiteralPath 'artifacts/driver-build/Release/driver_flugelkranz.dll' -Destination (Join-Path $driverPath 'bin/win64')
    Copy-Item -LiteralPath 'native/driver/driver.vrdrivermanifest' -Destination $driverPath
    Copy-Item -LiteralPath 'native/driver/resources' -Destination $driverPath -Recurse -Force
    Copy-Item -LiteralPath 'native/driver/vendor/openvr/LICENSE' -Destination (Join-Path $driverPath 'LICENSE-OpenVR')
    Copy-Item -LiteralPath 'native/driver/vendor/minhook/LICENSE.txt' -Destination (Join-Path $driverPath 'LICENSE-MinHook.txt')
    Copy-Item -LiteralPath 'install-driver.ps1' -Destination $packagePath
    New-Item -ItemType Directory -Force (Join-Path $packagePath 'native/driver') | Out-Null
    Copy-Item -LiteralPath 'native/driver/README.md' -Destination (Join-Path $packagePath 'native/driver/README.md')
    if ($SingleFile) {
        $publishArguments += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:DebugType=embedded', '-p:PublishTrimmed=false',
            "-p:DriverPackageDirectory=$packagePath")
        & $Dotnet @publishArguments
        if ($LASTEXITCODE -ne 0) { throw 'Application publish failed' }
        $outputFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File)
        if ($outputFiles.Count -ne 1 -or $outputFiles[0].Name -ne 'FlugelKranz.exe') {
            throw 'Single-file publish left unexpected files. The package is not ready to distribute.'
        }
    }
    Write-Host "Build output: $OutputDirectory"
} finally { Pop-Location }
