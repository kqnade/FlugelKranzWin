param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit,
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [switch]$Prerelease,
    [DateTime]$UtcNow = [DateTime]::UtcNow
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$releaseKind = if ($Prerelease) { 'PreRelease' } else { 'Release' }
$tagPattern = '^' + $releaseKind + '-[0-9]{10}$'
$prereleaseFlag = if ($Prerelease) { '--prerelease=true' } else { '--prerelease=false' }

function Invoke-GitHub {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: $($Arguments[0]) $($Arguments[1])" }
    return ($output -join "`n")
}

$ArtifactDirectory = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$files = @('FlugelKranz.exe', 'FlugelKranz-source.zip', 'build-info.json')
$lines = @(Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'SHA256SUMS.txt'))
if ($lines.Count -ne $files.Count) { throw 'Unexpected checksum manifest.' }
foreach ($name in $files) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $ArtifactDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if (@($lines | Where-Object { $_ -ceq "$hash  $name" }).Count -ne 1) { throw "Checksum mismatch: $name" }
}
$info = Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'build-info.json') -Raw | ConvertFrom-Json
if ($info.commit -ne $Commit) { throw 'Artifact source commit differs from the workflow commit.' }

$pages = Invoke-GitHub api "repos/$Repository/releases?per_page=100" --paginate --slurp | ConvertFrom-Json
$releases = @($pages | ForEach-Object { $_ })
$existing = $releases | Where-Object { $_.target_commitish -eq $Commit -and $_.tag_name -cmatch $tagPattern } | Sort-Object draft | Select-Object -First 1
if ($existing -and !$existing.draft) {
    Write-Host "Already released: $($existing.html_url)"
    $global:LASTEXITCODE = 0
    return
}

if ($existing) {
    $tag = $existing.tag_name
} else {
    # Release dates follow Japan time, independently of the runner's timezone.
    $prefix = $releaseKind + '-' + $UtcNow.ToUniversalTime().AddHours(9).ToString('yyyyMMdd')
    $refs = @(Invoke-GitHub api "repos/$Repository/git/matching-refs/tags/$prefix" | ConvertFrom-Json)
    $names = @($refs | ForEach-Object { $_.ref -replace '^refs/tags/', '' }) + @($releases | ForEach-Object { $_.tag_name })
    $numbers = @($names | Where-Object { $_ -cmatch "^$prefix[0-9]{2}$" } | ForEach-Object { [int]$_.Substring($prefix.Length) })
    $last = if ($numbers.Count) { ($numbers | Measure-Object -Maximum).Maximum } else { 0 }
    if ($last -ge 99) { throw 'The daily release sequence is exhausted.' }
    $tag = $prefix + ('{0:D2}' -f ([int]$last + 1))
    Invoke-GitHub release create $tag --repo $Repository --target $Commit --title $tag --draft $prereleaseFlag --generate-notes | Out-Host
}

# Upload into a draft, then publish only after every verified file has arrived.
$assets = @($files + 'SHA256SUMS.txt' | ForEach-Object { Join-Path $ArtifactDirectory $_ })
Invoke-GitHub release upload $tag @assets --repo $Repository --clobber | Out-Host
# A resumed older draft must not displace a newer published release.
$newerRelease = $releases | Where-Object { !$_.draft -and $_.tag_name -cmatch $tagPattern -and $_.tag_name -cgt $tag } | Select-Object -First 1
$latest = if ($Prerelease -or $newerRelease) { '--latest=false' } else { '--latest' }
Invoke-GitHub release edit $tag --repo $Repository --draft=false $prereleaseFlag $latest | Out-Host
Write-Host "Published: https://github.com/$Repository/releases/tag/$tag"
$global:LASTEXITCODE = 0
