Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Assert-NoSecrets($Value, [string]$RelativePath) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return }
    if ($Value -is [System.Array]) { foreach ($item in $Value) { Assert-NoSecrets $item $RelativePath }; return }
    foreach ($property in $Value.PSObject.Properties) {
        if ($property.Name -match '^(?i:pin|password|secret|token|access_token|refresh_token)$' -and $null -ne $property.Value -and [string]$property.Value -ne '') { throw "Payload contains a sensitive configuration field: $RelativePath" }
        Assert-NoSecrets $property.Value $RelativePath
    }
}
function Get-ProductVersion([string]$Path, [string]$Tag) {
    $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($value.version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Only stable three-part versions are supported' }
    foreach ($part in $value.version.Split('.')) { if ([long]$part -gt 65535) { throw 'Version exceeds Windows file version range' } }
    if ($Tag -and $Tag -ne "v$($value.version)") { throw 'Git tag differs from product version' }
    if ($value.productId -ne 'usb-web-launcher' -or $value.channel -ne 'stable') { throw 'Invalid product identity or channel' }
    $id = [guid]$value.installerAppId
    $value | Add-Member -NotePropertyName fileVersion -NotePropertyValue "$($value.version).0"
    return $value
}
function Assert-CleanPayload([string]$Directory, [switch]$SkipRequiredFiles) {
    $root = [IO.Path]::GetFullPath($Directory)
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\','/')
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Payload contains reparse point: $relative" }
        if ($relative -match '(?i)(^|[\\/])(browser-sessions|logs?|cache|testdata|node_modules|\.git|backups?)([\\/]|$)|\.WebView2([\\/]|$)|download-settings\.json|\.env($|\.)|\.(pdf|pfx|p12|pem|key|log|bak|tmp|pdb|zip)$') { throw "Forbidden payload file: $relative" }
        if ($file.PSIsContainer) { continue }
        if ($file.Extension -notin @('.exe','.dll','.json','.js','.png','.txt','.xml','.config','.dat','.bin','.ps1','.psm1')) { throw "Unapproved payload file type: $relative" }
        if ($file.Extension -in @('.json','.config','.xml','.js')) {
            $content = [IO.File]::ReadAllText($file.FullName)
            if ($file.Extension -eq '.json') { Assert-NoSecrets ($content | ConvertFrom-Json) $relative }
            if ($content -match '(?i)[A-Z]:\\|--remote-debugging|(?:password|access_token|refresh_token)"\s*:\s*"[^"\s]+') { throw "Payload contains machine-specific or sensitive configuration: $relative" }
        }
    }
    if (-not $SkipRequiredFiles) {
        foreach ($required in @('UsbWebLauncher.App.exe','UsbWebLauncher.App.dll','UsbWebLauncher.Core.dll','UsbWebLauncher.Windows.dll','launcher-profile.json','browser-extensions/ccb-ukey/manifest.json','browser-extensions/ccb-ukey/background.js','broker/UsbWebLauncher.NativePromptBroker.exe','broker/native-prompt-policy.json','installer/Invoke-Install.ps1')) {
            if (-not (Test-Path -LiteralPath (Join-Path $root $required) -PathType Leaf)) { throw "Required payload file missing: $required" }
        }
        $policy = Get-Content (Join-Path $root 'broker/native-prompt-policy.json') -Raw | ConvertFrom-Json
        if ($policy.Prompt.ProcessPath -or $policy.Prompt.ProcessSha256) { throw 'Broker policy must be unbound in release payload' }
    }
    Write-Host "Payload validation passed ($(@($files | Where-Object { -not $_.PSIsContainer }).Count) files)."
}
function Write-PayloadManifest([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory)
    $entries = @(Get-ChildItem $root -Recurse -File | Sort-Object FullName | ForEach-Object {
        @{ path = $_.FullName.Substring($root.Length).TrimStart('\','/').Replace('\','/'); size = $_.Length; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    @{ schemaVersion = 1; files = $entries } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'payload-manifest.json') -Encoding UTF8
}
function Remove-PublishedSdkDocumentation([string]$Directory) {
    # These SDK IntelliSense documents are not runtime assets and contain vendor
    # sample machine paths. Remove only these exact root files, never XML configs.
    foreach ($name in @('Microsoft.Web.WebView2.Core.xml','Microsoft.Web.WebView2.WinForms.xml','Microsoft.Web.WebView2.Wpf.xml')) {
        $path = Join-Path ([IO.Path]::GetFullPath($Directory)) $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
    }
}
Export-ModuleMember -Function Get-ProductVersion,Assert-CleanPayload,Write-PayloadManifest,Remove-PublishedSdkDocumentation
