[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AppDirectory,[Parameter(Mandatory=$true)][string]$InstallerAppId)
$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'InstallDirectory.psm1') -Force
    $existing = Get-ExistingInstallDirectory $InstallerAppId
    $path = Assert-InstallDirectory $AppDirectory -ExistingDirectory $existing -SkipOwnership:(Test-Path -LiteralPath "$AppDirectory.transaction")
    Test-InstallDirectoryWritable $path
    exit 0
} catch {
    $_.Exception.Message | Set-Content -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) 'UsbWebLauncher-installer-error.txt') -Encoding UTF8
    exit 1
}
