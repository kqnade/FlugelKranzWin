$ErrorActionPreference = 'Stop'
if (Get-Variable -Name FlugelKranzReleaseTestState -Scope Global -ErrorAction SilentlyContinue) {
    throw 'Release test state is already in use.'
}
$work = Join-Path $PSScriptRoot ('../artifacts/ci/release-tests/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$work = (Resolve-Path -LiteralPath $work).Path
$commit = 'a' * 40
$otherCommit = 'b' * 40
$publishScript = Join-Path $PSScriptRoot '../.github/scripts/Publish-Release.ps1'
$jstDay = [DateTime]::new(2026, 11, 8, 15, 0, 0, [DateTimeKind]::Utc)

function Assert-ReleaseTest([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
function Reset-ReleaseTest {
    $global:FlugelKranzReleaseTestState = @{
        Releases = @(); EarlierPage = @(); Refs = @(); Calls = [Collections.Generic.List[object]]::new(); Fail = ''
    }
}
function gh {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $state = $global:FlugelKranzReleaseTestState
    $state.Calls.Add($Arguments)
    $global:LASTEXITCODE = 0
    if ($Arguments[0] -eq 'api') {
        if ($Arguments[1] -like '*/releases?per_page=100') {
            # --paginate --slurp returns pages, not one flattened release array.
            $pages = [Collections.Generic.List[object]]::new()
            $pages.Add(@($state.EarlierPage))
            $pages.Add(@($state.Releases))
            return ConvertTo-Json -InputObject $pages.ToArray() -Depth 8 -Compress
        }
        if ($Arguments[1] -like '*/git/matching-refs/tags/*') {
            return ConvertTo-Json -InputObject @($state.Refs) -Depth 8 -Compress
        }
        throw 'Unexpected API endpoint in release test.'
    }
    if ($Arguments[0] -ne 'release') { throw 'Unexpected GitHub command in release test.' }
    if ($state.Fail -eq $Arguments[1]) { $global:LASTEXITCODE = 1; return 'Simulated GitHub failure.' }
    return 'Simulated GitHub success (no network request).'
}

function Run-Publish([DateTime]$Time = $jstDay) {
    & $publishScript -Repository 'test-owner/test-repo' -Commit $commit -ArtifactDirectory $work -UtcNow $Time
}
function Release-Calls([string]$Operation) {
    @($global:FlugelKranzReleaseTestState.Calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq $Operation })
}
function Expect-PublishFailure {
    $failed = $false
    try { Run-Publish } catch { $failed = $true }
    Assert-ReleaseTest $failed 'Expected publishing to fail.'
}

try {
    Assert-ReleaseTest ((Get-Command gh).CommandType -eq 'Function') 'GitHub command must be mocked.'
    Set-Content -LiteralPath (Join-Path $work 'FlugelKranz.exe') -Value 'test executable fixture'
    Set-Content -LiteralPath (Join-Path $work 'FlugelKranz-source.zip') -Value 'test source fixture'
    @{ commit = $commit } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'build-info.json')
    $lines = foreach ($file in @('FlugelKranz.exe', 'FlugelKranz-source.zip', 'build-info.json')) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $work $file)).Hash.ToLowerInvariant()
        "$hash  $file"
    }
    Set-Content -LiteralPath (Join-Path $work 'SHA256SUMS.txt') -Value $lines

    Reset-ReleaseTest
    Run-Publish $jstDay.AddSeconds(-1)
    $created = @(Release-Calls create)
    Assert-ReleaseTest ($created.Count -eq 1 -and $created[0][2] -eq 'Release-2026110801') 'Wrong release date before JST midnight.'

    Reset-ReleaseTest
    Run-Publish
    $created = @(Release-Calls create)
    Assert-ReleaseTest ($created.Count -eq 1 -and $created[0][2] -eq 'Release-2026110901') 'Wrong first release after JST midnight.'
    Assert-ReleaseTest ($created[0] -contains '--draft' -and $created[0] -contains $commit) 'Release must start as a draft at the tested commit.'
    $uploaded = @(Release-Calls upload)
    Assert-ReleaseTest ($uploaded.Count -eq 1 -and $uploaded[0] -contains (Join-Path $work 'FlugelKranz.exe')) 'Verified executable was not uploaded.'
    foreach ($asset in @('FlugelKranz.exe', 'FlugelKranz-source.zip', 'build-info.json', 'SHA256SUMS.txt')) {
        Assert-ReleaseTest ($uploaded[0] -contains (Join-Path $work $asset)) "Missing upload: $asset"
    }
    Assert-ReleaseTest ($uploaded[0][2] -eq 'Release-2026110901' -and $uploaded[0] -contains 'test-owner/test-repo') 'Upload used the wrong release or repository.'
    $edited = @(Release-Calls edit)
    Assert-ReleaseTest ($edited.Count -eq 1 -and $edited[0][2] -eq 'Release-2026110901' -and $edited[0] -contains 'test-owner/test-repo' -and $edited[0] -contains '--draft=false' -and $edited[0] -contains '--latest') 'Draft was not published as the latest release.'

    Reset-ReleaseTest
    $global:FlugelKranzReleaseTestState.Refs = @(@{ ref = 'refs/tags/Release-2026110901' })
    $global:FlugelKranzReleaseTestState.Releases = @(@{ tag_name = 'Release-2026110902'; target_commitish = $otherCommit; draft = $true })
    Run-Publish
    $created = @(Release-Calls create)
    Assert-ReleaseTest ($created[0][2] -eq 'Release-2026110903') 'Existing tags and drafts must reserve their sequence numbers.'

    Reset-ReleaseTest
    $global:FlugelKranzReleaseTestState.Releases = @(@{ tag_name = 'Release-2026110801'; target_commitish = $commit; draft = $false; html_url = 'https://example.invalid/already-released' })
    Run-Publish
    Assert-ReleaseTest (@(Release-Calls create).Count -eq 0 -and @(Release-Calls upload).Count -eq 0) 'Published commit must not be republished or overwritten.'

    Reset-ReleaseTest
    $global:FlugelKranzReleaseTestState.Releases = @(@{ tag_name = 'Release-2026110801'; target_commitish = $commit; draft = $true })
    Run-Publish
    Assert-ReleaseTest (@(Release-Calls create).Count -eq 0) 'Retry must reuse its draft.'
    $uploaded = @(Release-Calls upload)
    Assert-ReleaseTest ($uploaded[0][2] -eq 'Release-2026110801') 'Retry changed the reserved release name.'

    Reset-ReleaseTest
    $global:FlugelKranzReleaseTestState.EarlierPage = @(@{ tag_name = 'Release-2026110901'; target_commitish = $otherCommit; draft = $false })
    $global:FlugelKranzReleaseTestState.Releases = @(@{ tag_name = 'Release-2026110801'; target_commitish = $commit; draft = $true })
    Run-Publish
    $edited = @(Release-Calls edit)
    Assert-ReleaseTest (@(Release-Calls create).Count -eq 0 -and $edited[0] -contains '--latest=false') 'An older draft retry must not displace a newer release.'

    Reset-ReleaseTest
    $global:FlugelKranzReleaseTestState.EarlierPage = @(@{ tag_name = 'Release-2026110901'; target_commitish = $commit; draft = $true })
    $global:FlugelKranzReleaseTestState.Releases = @(@{ tag_name = 'Release-2026110801'; target_commitish = $commit; draft = $false })
    Run-Publish
    Assert-ReleaseTest (@(Release-Calls upload).Count -eq 0 -and @(Release-Calls edit).Count -eq 0) 'Published commit on a later API page must take precedence over a draft.'

    Reset-ReleaseTest
    $global:FlugelKranzReleaseTestState.Fail = 'upload'
    Expect-PublishFailure
    Assert-ReleaseTest (@(Release-Calls edit).Count -eq 0) 'Failed upload must not publish the draft.'

    Reset-ReleaseTest
    $global:FlugelKranzReleaseTestState.Refs = @(@{ ref = 'refs/tags/Release-2026110999' })
    Expect-PublishFailure
    Assert-ReleaseTest (@(Release-Calls create).Count -eq 0) 'Daily sequence overflow must not create a release.'

    Reset-ReleaseTest
    Add-Content -LiteralPath (Join-Path $work 'FlugelKranz.exe') -Value 'tampered'
    Expect-PublishFailure
    Assert-ReleaseTest ($global:FlugelKranzReleaseTestState.Calls.Count -eq 0) 'Invalid checksums must fail before any GitHub request.'
    Write-Host 'Release workflow verification passed (10 cases, no network requests).'
} finally {
    Remove-Variable -Name FlugelKranzReleaseTestState -Scope Global -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE = 0
