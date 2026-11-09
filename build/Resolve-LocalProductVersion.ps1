[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$ManifestUrl = 'https://github.com/yxsimony-dot/CCB-USB-User/releases/latest/download/release-manifest.json',
    [string]$CachePath,
    [string]$VersionFloorPath,
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not $CachePath) {
    $CachePath = Join-Path $root 'artifacts/local-version/latest-release-manifest.json'
}
$CachePath = [IO.Path]::GetFullPath($CachePath)
if (-not $VersionFloorPath) {
    $VersionFloorPath = Join-Path $root 'build/local-version-floor.json'
}
$VersionFloorPath = [IO.Path]::GetFullPath($VersionFloorPath)

function ConvertTo-StableVersion([string]$Value) {
    if ($Value -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        throw "Invalid stable version: $Value"
    }
    $parts = @($Value.Split('.') | ForEach-Object { [int]$_ })
    if ($parts | Where-Object { $_ -gt 65535 }) {
        throw "Version exceeds Windows file version range: $Value"
    }
    return [version]$Value
}

function Get-NextStableVersion([version]$Version) {
    if ($Version.Build -lt 65535) {
        return "$($Version.Major).$($Version.Minor).$($Version.Build + 1)"
    }
    if ($Version.Minor -lt 65535) {
        return "$($Version.Major).$($Version.Minor + 1).0"
    }
    if ($Version.Major -lt 65535) {
        return "$($Version.Major + 1).0.0"
    }
    throw 'No higher Windows-compatible product version is available.'
}

function Read-VersionFloor([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    $value = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
    if ($value.schemaVersion -ne 1 -or -not $value.minimumVersion) {
        throw "Invalid local version floor: $Path"
    }
    return ConvertTo-StableVersion ([string]$value.minimumVersion)
}

function Get-VersionAtOrAboveFloor([version]$Candidate, [version]$Floor) {
    if ($Floor -and $Floor -gt $Candidate) {
        return $Floor.ToString(3)
    }
    return $Candidate.ToString(3)
}

function ConvertFrom-ReleaseManifest([string]$Text) {
    $manifest = $Text.TrimStart([char]0xfeff) | ConvertFrom-Json
    $version = ConvertTo-StableVersion ([string]$manifest.version)
    if ($manifest.productId -ne 'usb-web-launcher' -or
        $manifest.channel -ne 'stable' -or
        [string]$manifest.sourceRepository -ne 'https://github.com/yxsimony-dot/CCB-USB-User' -or
        [string]$manifest.sourceCommit -notmatch '^[a-fA-F0-9]{40}$') {
        throw 'Latest release manifest has an invalid product identity.'
    }
    return [pscustomobject]@{
        Version = $version
        SourceCommit = ([string]$manifest.sourceCommit).ToLowerInvariant()
        Text = $Text
    }
}

function Read-LatestRelease {
    $downloadError = $null
    if (-not $Offline) {
        try {
            $client = New-Object Net.WebClient
            try {
                $client.Headers['User-Agent'] = 'UsbWebLauncher-local-build'
                $text = $client.DownloadString($ManifestUrl)
            } finally {
                $client.Dispose()
            }
            $release = ConvertFrom-ReleaseManifest $text
            $cacheDirectory = Split-Path $CachePath -Parent
            if (-not (Test-Path -LiteralPath $cacheDirectory -PathType Container)) {
                New-Item -Path $cacheDirectory -ItemType Directory -Force | Out-Null
            }
            $utf8 = New-Object Text.UTF8Encoding($false)
            [IO.File]::WriteAllText($CachePath, $release.Text, $utf8)
            return $release
        } catch {
            $downloadError = $_
        }
    }
    if (Test-Path -LiteralPath $CachePath -PathType Leaf) {
        try {
            return ConvertFrom-ReleaseManifest ([IO.File]::ReadAllText($CachePath))
        } catch {
            if (-not $downloadError) {
                $downloadError = $_
            }
        }
    }
    return [pscustomobject]@{ Release = $null; Error = $downloadError }
}

function Find-Git {
    $command = Get-Command git -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }
    foreach ($candidate in @(
        "$env:ProgramFiles\Git\cmd\git.exe",
        "$env:ProgramFiles\Git\bin\git.exe",
        "${env:ProgramFiles(x86)}\Git\cmd\git.exe"
    )) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }
    return $null
}

function Invoke-Git([string]$GitPath, [string[]]$Arguments) {
    $output = @(& $GitPath -C $root @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git $($Arguments -join ' ')"
    }
    return $output
}

function Test-GitObject([string]$GitPath, [string]$Commit) {
    & $GitPath -C $root cat-file -e "$Commit^{commit}" 2>$null
    return $LASTEXITCODE -eq 0
}

function Test-GitAncestor([string]$GitPath, [string]$Ancestor, [string]$Descendant) {
    & $GitPath -C $root merge-base --is-ancestor $Ancestor $Descendant 2>$null
    if ($LASTEXITCODE -eq 0) {
        return $true
    }
    if ($LASTEXITCODE -eq 1) {
        return $false
    }
    throw "Unable to compare Git commits $Ancestor and $Descendant."
}

function Read-LatestTaggedRelease([string]$GitPath) {
    $candidates = @()
    foreach ($tag in Invoke-Git $GitPath @('tag', '--merged', 'HEAD', '--list', 'v[0-9]*')) {
        if ($tag -match '^v((0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*))$') {
            $parsed = ConvertTo-StableVersion $Matches[1]
            $candidates += [pscustomobject]@{ Tag = $tag; Version = $parsed }
        }
    }
    $latest = $candidates | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $latest) {
        return $null
    }
    $commit = (Invoke-Git $GitPath @('rev-list', '-n', '1', $latest.Tag) | Select-Object -First 1).Trim().ToLowerInvariant()
    return [pscustomobject]@{ Version = $latest.Version; SourceCommit = $commit }
}

$gitPath = Find-Git
$releaseResult = Read-LatestRelease
$release = if ($releaseResult.PSObject.Properties.Name -contains 'Release') { $releaseResult.Release } else { $releaseResult }
$versionFloor = Read-VersionFloor $VersionFloorPath

if (-not $release -and $gitPath) {
    $release = Read-LatestTaggedRelease $gitPath
}
if (-not $release) {
    $reason = if ($releaseResult.Error) { $releaseResult.Error.Exception.Message } else { 'no cached manifest or release tag is available' }
    throw "Cannot determine the latest product version: $reason"
}

if (-not $gitPath) {
    Write-Output (Get-VersionAtOrAboveFloor ([version](Get-NextStableVersion $release.Version)) $versionFloor)
    exit 0
}

$head = (Invoke-Git $gitPath @('rev-parse', 'HEAD') | Select-Object -First 1).Trim().ToLowerInvariant()
$dirty = @(Invoke-Git $gitPath @('status', '--porcelain', '--untracked-files=all')).Count -gt 0
$sourceCommit = $release.SourceCommit

if ($head -eq $sourceCommit) {
    $candidate = if ($dirty) { [version](Get-NextStableVersion $release.Version) } else { $release.Version }
    Write-Output (Get-VersionAtOrAboveFloor $candidate $versionFloor)
    exit 0
}

if (-not (Test-GitObject $gitPath $sourceCommit)) {
    throw "Latest release commit $sourceCommit is not available locally. Fetch or pull the repository before building."
}
if (Test-GitAncestor $gitPath $head $sourceCommit) {
    throw "Local source $head is older than latest release source $sourceCommit. Pull the latest source before building."
}
if (-not (Test-GitAncestor $gitPath $sourceCommit $head)) {
    throw "Local source $head has diverged from latest release source $sourceCommit. Reconcile the branch before building."
}

Write-Output (Get-VersionAtOrAboveFloor ([version](Get-NextStableVersion $release.Version)) $versionFloor)
