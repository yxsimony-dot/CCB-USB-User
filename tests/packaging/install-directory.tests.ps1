$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../../installer/InstallDirectory.psm1') -Force
function Assert($condition,$message) { if (-not $condition) { throw "FAIL: $message" } }
function Reject([scriptblock]$action) { try { & $action | Out-Null } catch { return }; throw 'FAIL: invalid directory accepted' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('uwl-directory-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $app = Join-Path $root 'Chinese 名称 with spaces'
    Assert ((Assert-InstallDirectory $app) -eq $app) 'Chinese and spaces not accepted'
    Assert (-not (Test-Path -LiteralPath $app)) 'validation created application directory'
    Assert ((Assert-InstallDirectory $app -ExistingDirectory ($app.ToUpperInvariant() + '\')) -eq $app) 'upgrade normalization'
    Reject { Assert-InstallDirectory (Join-Path $root 'different') -ExistingDirectory $app }
    foreach ($path in @('relative\app','C:app','\\server\share\app','\\?\C:\app','C:\',"$root\..\app","$root\trailing.","$root\bad&name","$root\bad[name]","$root\CON")) { Reject { Assert-InstallDirectory $path } }
    foreach ($path in @($env:SystemRoot, "$env:SystemRoot\child", $env:ProgramFiles, "$env:ProgramFiles\child", [Environment]::GetFolderPath('UserProfile'), [Environment]::GetFolderPath('LocalApplicationData'), (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'UsbWebLauncher\updates'))) { Reject { Assert-InstallDirectory $path } }
    New-Item -ItemType Directory -Path $app | Out-Null
    Set-Content -LiteralPath "$app/user.pdf" 'preserve'
    Reject { Assert-InstallDirectory $app }
    Assert ((Get-Content -LiteralPath "$app/user.pdf") -eq 'preserve') 'validation changed user file'
    Remove-Item -LiteralPath "$app/user.pdf"
    Set-Content -LiteralPath "$app/app.txt" 'owned'
    Set-Content -LiteralPath "$app/payload-manifest.json" '{"files":[{"path":"app.txt"}]}'
    $null = Assert-InstallDirectory $app -ExistingDirectory $app
    # Inject an in-memory registry factory: never read/write the product's real
    # registration or pending-install key while testing upgrade resolution.
    $module = Get-Module InstallDirectory
    $originalFactory = & $module { (Get-Command Open-InstallerRegistry).ScriptBlock }
    $fakeHive = [pscustomobject]@{ Keys = @{} }
    $fakeHive | Add-Member ScriptMethod OpenSubKey { param($name) return $this.Keys[$name] }
    $fakeHive | Add-Member ScriptMethod Dispose { }
    function FakeKey($values) {
        $key = [pscustomobject]@{ Values = $values }
        $key | Add-Member ScriptMethod GetValue { param($name,$default,$options) if ($this.Values.ContainsKey($name)) { return $this.Values[$name] }; return $default }
        $key | Add-Member ScriptMethod Dispose { }
        return $key
    }
    & $module { param($hive) $script:TestRegistryHive = $hive; function script:Open-InstallerRegistry { return $script:TestRegistryHive } } $fakeHive
    try {
        $id = '12345678-1234-1234-1234-123456789012'
        $registration = "Software\Microsoft\Windows\CurrentVersion\Uninstall\{$id}_is1"
        $pendingKey = 'Software\UsbWebLauncher\Installer'
        $fakeHive.Keys[$registration] = FakeKey @{InstallLocation=$app}
        Assert ((Get-ExistingInstallDirectory $id) -eq $app) 'registered custom upgrade location lost'
        $fakeHive.Keys[$registration] = FakeKey @{}
        Reject { Get-ExistingInstallDirectory $id }
        $fakeHive.Keys.Remove($registration)
        $fakeHive.Keys[$pendingKey] = FakeKey @{PendingInstallLocation=$app}
        Assert ((Get-ExistingInstallDirectory $id) -eq $app) 'interrupted first custom install location lost'
        $fakeHive.Keys[$registration] = FakeKey @{InstallLocation=(Join-Path $root 'different')}
        Reject { Get-ExistingInstallDirectory $id }
        $fakeHive.Keys[$pendingKey] = FakeKey @{PendingInstallLocation=(Join-Path $root 'never-created')}
        Assert ((Get-ExistingInstallDirectory $id) -eq (Join-Path $root 'different')) 'pre-transaction crash marker blocks registered app'
    } finally {
        & $module { param($factory) Set-Item Function:script:Open-InstallerRegistry $factory; Remove-Variable TestRegistryHive -Scope Script } $originalFactory
    }
    Test-InstallDirectoryWritable $app
    Assert (@(Get-ChildItem -LiteralPath $root -Filter '.usb-web-launcher-write-probe-*').Count -eq 0) 'write probe not cleaned'
    $junction = Join-Path $root 'junction'
    New-Item -ItemType Junction -Path $junction -Target $app | Out-Null
    Reject { Assert-InstallDirectory "$junction/nested" }
    (Get-Item -LiteralPath $junction).Delete()
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../../installer/Invoke-Install.ps1'),[ref]$tokens,[ref]$errors)
    Assert ($errors.Count -eq 0) 'installer syntax invalid'
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Remove-InstalledPayload' },$true)
    . ([scriptblock]::Create($function.Extent.Text))
    Set-Content -LiteralPath "$app/user.pdf" 'keep on uninstall'
    Remove-InstalledPayload $app
    Assert (-not (Test-Path -LiteralPath "$app/app.txt")) 'owned custom-location payload not removed'
    Assert ((Get-Content -LiteralPath "$app/user.pdf") -eq 'keep on uninstall') 'uninstall deleted user PDF'
    Assert (Test-Path -LiteralPath $app) 'uninstall recursively removed user directory'
    Write-Host 'PASS: selectable directory, Chinese/spaces, upgrade lock, roots/system/data/relative/reparse paths, ownership and nonmutating validation'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
