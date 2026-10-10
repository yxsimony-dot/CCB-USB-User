# Windows PowerShell 5.1 compatible. Never receives PIN or bank credentials.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Install','Recover','Commit','Uninstall')][string]$Action,
    [Parameter(Mandatory=$true)][string]$AppDirectory,
    [string]$PayloadDirectory,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Transaction.psm1') -Force
function Run-App([string]$Directory,[string]$Command) {
    $exe = Join-Path $Directory 'UsbWebLauncher.App.exe'
    $process = Start-Process -FilePath $exe -ArgumentList $Command -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Application operation $Command failed (exit $($process.ExitCode)); installation is not ready." }
}
function Close-Application([string]$Directory) {
    $exe = Join-Path $Directory 'UsbWebLauncher.App.exe'
    foreach ($process in Get-Process -Name 'UsbWebLauncher.App' -ErrorAction SilentlyContinue) {
        try { $path = $process.MainModule.FileName } catch { throw 'Cannot establish ownership of a running app. Close it and retry.' }
        if ([string]::Equals($path,$exe,[StringComparison]::OrdinalIgnoreCase)) {
            if (-not $process.CloseMainWindow()) { throw 'Close the running application normally, then retry installation.' }
            if (-not $process.WaitForExit(30000)) { throw 'Application is still closing. Wait for business activity to stop, then retry.' }
        }
    }
}
function Assert-Payload([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $manifest = Get-Content (Join-Path $Directory 'payload-manifest.json') -Raw | ConvertFrom-Json
    $declared = @($manifest.files | ForEach-Object { $_.path.Replace('/','\') })
    if (@($declared | Select-Object -Unique).Count -ne $declared.Count) { throw 'Duplicate payload manifest paths' }
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force -Recurse) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Payload contains a reparse point' }
        if (-not $item.PSIsContainer) {
            $relative = $item.FullName.Substring($root.Length)
            if ($relative -ne 'payload-manifest.json' -and $relative -notin $declared) { throw 'Payload contains an undeclared file' }
        }
    }
    foreach ($entry in $manifest.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $Directory $entry.path))
        if (-not $path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid payload path' }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $entry.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Installation payload integrity check failed' }
    }
}
function Save-UninstallRegistration {
    $product = Get-Content (Join-Path $PSScriptRoot 'product.json') -Raw | ConvertFrom-Json
    $key = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + ([guid]$product.installerAppId).ToString().ToUpperInvariant() + '}_is1'
    $exists = Test-Path ('Registry::HKEY_CURRENT_USER\' + $key.Substring(5))
    @{ existed = $exists } | ConvertTo-Json | Set-Content "$AppDirectory.transaction/uninstall-registration.json" -Encoding UTF8
    if ($exists) {
        & "$env:SystemRoot/System32/reg.exe" export $key "$AppDirectory.transaction/uninstall-registration.reg" /y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Cannot back up uninstall registration' }
    }
}
function Restore-UninstallRegistration {
    $metadata = "$AppDirectory.transaction/uninstall-registration.json"
    if (-not (Test-Path $metadata)) { return }
    $record = Get-Content $metadata -Raw | ConvertFrom-Json
    $product = Get-Content (Join-Path $PSScriptRoot 'product.json') -Raw | ConvertFrom-Json
    $key = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + ([guid]$product.installerAppId).ToString().ToUpperInvariant() + '}_is1'
    if ($record.existed -and -not (Test-Path "$AppDirectory.transaction/uninstall-registration.reg")) { throw 'Uninstall registration backup is missing' }
    $registryPath = 'Registry::HKEY_CURRENT_USER\' + $key.Substring(5)
    if (Test-Path $registryPath) { Remove-Item -LiteralPath $registryPath -Recurse -Force }
    if ($record.existed) {
        & "$env:SystemRoot/System32/reg.exe" import "$AppDirectory.transaction/uninstall-registration.reg" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Cannot restore uninstall registration; transaction retained' }
    }
}
try {
    $expected = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/UsbWebLauncher'
    if (-not [string]::Equals([IO.Path]::GetFullPath($AppDirectory).TrimEnd('\'),[IO.Path]::GetFullPath($expected).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Only the fixed current-user installation directory is supported' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run the installer normally as the intended Windows user, not as administrator' }
    $helper = if ($PayloadDirectory) { $PayloadDirectory } else { $AppDirectory }
    $register = { param($path) Run-App $path '--installer-register-broker' }
    $health = { param($path) Run-App $path '--installer-health-check' }
    $restore = { param($path) Run-App $helper '--installer-restore-broker'; Restore-UninstallRegistration }
    $finishRecovery = { param($path) Run-App $helper '--installer-commit-broker' }
    switch ($Action) {
        'Install' {
            Assert-Payload $PayloadDirectory
            $payloadBytes = (Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -File | Measure-Object Length -Sum).Sum
            $drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($AppDirectory))
            if ($drive.AvailableFreeSpace -lt ($payloadBytes * 1.1 + 50MB)) { throw 'Insufficient free disk space for verified staging and installation' }
            $prepare = { param($path) Run-App $helper '--installer-prepare'; Close-Application $path }
            $backup = { param($path) Save-UninstallRegistration; Run-App $helper '--installer-backup-broker' }
            Invoke-InstallTransaction $AppDirectory $PayloadDirectory $Version $prepare $register $health $backup $restore -DeferCommit -FinishRecovery $finishRecovery -ValidateStaged { param($path) Assert-Payload $path }
        }
        'Recover' { Close-Application $AppDirectory; Restore-InstallTransaction $AppDirectory $register $health $restore $finishRecovery }
        'Commit' { Complete-InstallTransaction $AppDirectory { param($path) Run-App $helper '--installer-commit-broker' } }
        'Uninstall' {
            Run-App $helper '--installer-prepare'
            if (Test-Path "$AppDirectory.transaction") { throw 'An interrupted installation requires recovery before uninstalling' }
            Close-Application $AppDirectory
            if (Get-ChildItem -LiteralPath $AppDirectory -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Application directory contains a reparse point; uninstall stopped' }
            Run-App $helper '--installer-uninstall-broker'
            $manifest = Get-Content "$AppDirectory/payload-manifest.json" -Raw | ConvertFrom-Json
            $root = [IO.Path]::GetFullPath($AppDirectory).TrimEnd('\') + '\'
            foreach ($entry in $manifest.files) {
                $path = [IO.Path]::GetFullPath((Join-Path $AppDirectory $entry.path))
                if (-not $path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid uninstall manifest path' }
                if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
            }
            Remove-Item -LiteralPath "$AppDirectory/payload-manifest.json","$AppDirectory/installed-version.json" -Force -ErrorAction SilentlyContinue
        }
    }
    exit 0
} catch {
    # Error only includes technical metadata and paths, never user secrets.
    $_.Exception.Message | Set-Content -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) 'UsbWebLauncher-installer-error.txt') -Encoding UTF8
    Write-Error $_.Exception.Message
    exit 1
}
