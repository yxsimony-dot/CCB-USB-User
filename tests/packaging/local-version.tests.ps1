$ErrorActionPreference = 'Stop'

function Assert($Condition, [string]$Message) {
    if (-not $Condition) {
        throw "FAIL: $Message"
    }
}

function Find-Git {
    $command = Get-Command git -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in @(
        "$env:ProgramFiles\Git\cmd\git.exe",
        "$env:ProgramFiles\Git\bin\git.exe",
        "${env:ProgramFiles(x86)}\Git\cmd\git.exe"
    )) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    }
    throw 'Git is required for local version tests.'
}

$git = Find-Git
$resolver = Join-Path $PSScriptRoot '../../build/Resolve-LocalProductVersion.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('uwl-local-version-' + [guid]::NewGuid())
$repository = Join-Path $fixture 'repository'
$manifestPath = Join-Path $fixture 'release-manifest.json'
$cachePath = Join-Path $fixture 'cache/release-manifest.json'
$versionFloorPath = Join-Path $fixture 'local-version-floor.json'

try {
    New-Item -Path $repository -ItemType Directory -Force | Out-Null
    & $git -C $repository init --quiet
    & $git -C $repository config user.name 'Local Version Test'
    & $git -C $repository config user.email 'local-version@example.invalid'
    Set-Content -LiteralPath (Join-Path $repository 'source.txt') -Value 'released source' -Encoding ascii
    & $git -C $repository add .
    & $git -C $repository commit --quiet -m 'released source'
    Assert ($LASTEXITCODE -eq 0) 'fixture release commit created'
    $releaseCommit = (& $git -C $repository rev-parse HEAD).Trim()

    [ordered]@{
        schemaVersion = 1
        productId = 'usb-web-launcher'
        version = '1.1.19'
        channel = 'stable'
        sourceRepository = 'https://github.com/yxsimony-dot/CCB-USB-User'
        sourceCommit = $releaseCommit
    } | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
    $manifestUrl = ([uri]$manifestPath).AbsoluteUri

    $cleanVersion = (& $resolver -RepositoryRoot $repository -ManifestUrl $manifestUrl -CachePath $cachePath).Trim()
    Assert ($cleanVersion -eq '1.1.19') 'clean release source uses the latest published version'

    Set-Content -LiteralPath (Join-Path $repository 'source.txt') -Value 'local changes' -Encoding ascii
    $dirtyVersion = (& $resolver -RepositoryRoot $repository -Offline -CachePath $cachePath).Trim()
    Assert ($dirtyVersion -eq '1.1.20') 'dirty source advances beyond the latest published version'

    [ordered]@{
        schemaVersion = 1
        minimumVersion = '1.1.21'
    } | ConvertTo-Json | Set-Content -LiteralPath $versionFloorPath -Encoding utf8
    $flooredVersion = (& $resolver -RepositoryRoot $repository -Offline -CachePath $cachePath -VersionFloorPath $versionFloorPath).Trim()
    Assert ($flooredVersion -eq '1.1.21') 'local version floor advances beyond an already used local version'

    $floor = Get-Content -LiteralPath $versionFloorPath -Raw | ConvertFrom-Json
    $floor.minimumVersion = '1.1.18'
    $floor | ConvertTo-Json | Set-Content -LiteralPath $versionFloorPath -Encoding utf8
    $unreducedVersion = (& $resolver -RepositoryRoot $repository -Offline -CachePath $cachePath -VersionFloorPath $versionFloorPath).Trim()
    Assert ($unreducedVersion -eq '1.1.20') 'local version floor cannot reduce the release-derived version'

    & $git -C $repository add .
    & $git -C $repository commit --quiet -m 'local source ahead'
    Assert ($LASTEXITCODE -eq 0) 'fixture local commit created'
    $aheadVersion = (& $resolver -RepositoryRoot $repository -Offline -CachePath $cachePath).Trim()
    Assert ($aheadVersion -eq '1.1.20') 'committed source ahead advances beyond the latest published version'

    & $git -C $repository switch --quiet --detach $releaseCommit
    Set-Content -LiteralPath (Join-Path $repository 'source.txt') -Value 'second released source' -Encoding ascii
    & $git -C $repository add .
    & $git -C $repository commit --quiet -m 'newer release source'
    $newerReleaseCommit = (& $git -C $repository rev-parse HEAD).Trim()
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest.sourceCommit = $newerReleaseCommit
    $manifest.version = '1.1.20'
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
    & $git -C $repository switch --quiet --detach $releaseCommit

    $caught = $null
    try {
        & $resolver -RepositoryRoot $repository -ManifestUrl $manifestUrl -CachePath $cachePath | Out-Null
    } catch {
        $caught = $_.Exception.Message
    }
    Assert ($caught -like '*older than latest release source*') 'source older than the latest release is rejected'

    Write-Host 'PASS: local builds stay at or ahead of the latest release and reject stale source'
} finally {
    if (Test-Path -LiteralPath $fixture) {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
