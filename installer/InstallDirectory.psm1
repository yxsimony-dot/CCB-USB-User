# Windows PowerShell 5.1. Validation never creates or deletes an application directory.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Transaction.psm1')
function Open-InstallerRegistry {
    return [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry64)
}

function Get-ExistingInstallDirectory([string]$InstallerAppId) {
    $keyName = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + ([guid]$InstallerAppId).ToString().ToUpperInvariant() + '}_is1'
    $hive = Open-InstallerRegistry
    try {
        $pending = $hive.OpenSubKey('Software\UsbWebLauncher\Installer')
        $pendingLocation = ''
        if ($null -ne $pending) { try { $pendingLocation = [string]$pending.GetValue('PendingInstallLocation','') } finally { $pending.Dispose() } }
        if ($pendingLocation) {
            $pendingLocation = ConvertTo-InstallDirectory $pendingLocation
            if (-not (Test-Path -LiteralPath "$pendingLocation.transaction") -and -not (Test-Path -LiteralPath "$pendingLocation/payload-manifest.json") -and -not (Test-Path -LiteralPath "$pendingLocation/installed-version.json")) { $pendingLocation = '' }
        }
        $key = $hive.OpenSubKey($keyName)
        if ($null -ne $key) {
            try {
                $location = [string]$key.GetValue('InstallLocation','',[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if ([string]::IsNullOrWhiteSpace($location)) { throw 'Existing installation registration has no InstallLocation. Repair or uninstall it before installing again.' }
                if ($pendingLocation -and (ConvertTo-InstallDirectory $pendingLocation) -ne (ConvertTo-InstallDirectory $location)) { throw 'Existing and unfinished installation locations conflict. Preserve both locations for repair.' }
                return $location
            } finally { $key.Dispose() }
        }
        if ($pendingLocation) { return $pendingLocation }
    } finally { $hive.Dispose() }
    $legacy = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\UsbWebLauncher'
    # A first installation interrupted before Inno wrote its registration must
    # recover at its original location rather than replacing the shared broker.
    if ((Test-Path -LiteralPath "$legacy/installed-version.json") -or (Test-Path -LiteralPath "$legacy/payload-manifest.json") -or (Test-Path -LiteralPath "$legacy.transaction")) { return $legacy }
    return ''
}

function Set-PendingInstallDirectory([string]$AppDirectory) {
    $hive = Open-InstallerRegistry
    try {
        $key = $hive.CreateSubKey('Software\UsbWebLauncher\Installer')
        try {
            if ($AppDirectory) { $key.SetValue('PendingInstallLocation',$AppDirectory,[Microsoft.Win32.RegistryValueKind]::String) }
            else { $key.DeleteValue('PendingInstallLocation',$false) }
        } finally { $key.Dispose() }
    } finally { $hive.Dispose() }
}

function ConvertTo-InstallDirectory([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:\\' -or $Path -match '[\x00-\x1f"<>|?*\[\]&%`$]' -or $Path.Substring(2).Contains(':')) { throw 'Choose an absolute local drive folder; network paths and special characters are not supported.' }
    $trimmed = $Path.TrimEnd('\')
    foreach ($part in $trimmed.Substring(3).Split('\')) {
        if (-not $part -or $part -match '[. ]$' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Installation path contains an invalid or reserved folder name.' }
    }
    return [IO.Path]::GetFullPath($trimmed).TrimEnd('\')
}

function Assert-InstallDirectory {
    param([string]$AppDirectory, [string]$ExistingDirectory = '', [switch]$SkipOwnership)
    $full = ConvertTo-InstallDirectory $AppDirectory
    $drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($full))
    if (-not $drive.IsReady -or $drive.DriveType -ne [IO.DriveType]::Fixed) { throw 'Choose a folder on an available local fixed drive.' }
    if ($ExistingDirectory -and -not [string]::Equals($full,(ConvertTo-InstallDirectory $ExistingDirectory),[StringComparison]::OrdinalIgnoreCase)) { throw 'An existing installation must be upgraded at its current location. Uninstall it first to choose another folder; personal settings and PDFs are retained.' }
    $profile = [Environment]::GetFolderPath('UserProfile')
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    $blockedTrees = @($env:SystemRoot,$env:ProgramFiles,${env:ProgramFiles(x86)},$env:ProgramData,(Join-Path $local 'UsbWebLauncher'))
    foreach ($path in $blockedTrees) {
        if ($path -and ($full -eq $path.TrimEnd('\') -or $full.StartsWith($path.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase))) { throw 'Choose an ordinary user-writable application folder outside system and application-data directories.' }
    }
    $blockedRoots = @($profile,$local,[Environment]::GetFolderPath('ApplicationData'),[Environment]::GetFolderPath('MyDocuments'),[Environment]::GetFolderPath('Desktop'),(Join-Path $local 'Programs'),(Split-Path $profile -Parent))
    foreach ($path in $blockedRoots) { if ($path -and $full -eq $path.TrimEnd('\')) { throw 'Choose a dedicated application subfolder, not a user-data or shared parent directory.' } }
    # Check parents before recursively examining the selected folder.
    foreach ($target in @($full,"$full.transaction")) {
        $ancestor = $target
        while ($ancestor) {
            if (Test-Path -LiteralPath $ancestor) {
                $entry = Get-Item -LiteralPath $ancestor -Force
                if (-not $entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installation path must contain only ordinary directories, without junctions or symbolic links.' }
            }
            $ancestor = Split-Path $ancestor -Parent
        }
    }
    $null = Assert-TransactionPath $full
    if (-not $SkipOwnership) { Assert-OwnedInstallation $full }
    return $full
}

function Test-InstallDirectoryWritable([string]$AppDirectory) {
    # Probes the nearest existing parent with our own uniquely named file, never
    # changes permissions, never deletes a user folder, and leaves no app payload.
    $parent = Split-Path $AppDirectory -Parent
    while (-not (Test-Path -LiteralPath $parent)) { $parent = Split-Path $parent -Parent }
    $probe = Join-Path $parent ('.usb-web-launcher-write-probe-' + [guid]::NewGuid().ToString('N'))
    $stream = $null
    try { $stream = [IO.File]::Open($probe,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None) }
    catch { throw 'The selected location is not writable by this Windows user. Choose another folder.' }
    finally { if ($null -ne $stream) { $stream.Dispose(); Remove-Item -LiteralPath $probe -Force } }
}
Export-ModuleMember -Function Get-ExistingInstallDirectory,Assert-InstallDirectory,Test-InstallDirectoryWritable,Set-PendingInstallDirectory
