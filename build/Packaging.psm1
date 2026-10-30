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
function Get-ProductVersion {
    [CmdletBinding()]
    param([string]$Path, [string]$Tag, [Nullable[long]]$ReleaseSequence)

    $value = [IO.File]::ReadAllText([IO.Path]::GetFullPath($Path)) | ConvertFrom-Json
    if ($value.version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Only stable three-part versions are supported' }
    foreach ($part in $value.version.Split('.')) { if ([long]$part -gt 65535) { throw 'Version exceeds Windows file version range' } }
    $baseVersion = $value.version
    if ($null -ne $ReleaseSequence) {
        if ($ReleaseSequence -le 0 -or $ReleaseSequence -gt 65535) { throw 'Release sequence must be between 1 and 65535' }
        $parts = $value.version.Split('.')
        $patch = [long]$parts[2] + $ReleaseSequence
        if ($patch -gt 65535) { throw 'Effective version exceeds Windows file version range' }
        $value.version = "$($parts[0]).$($parts[1]).$patch"
    }
    $value | Add-Member -NotePropertyName baseVersion -NotePropertyValue $baseVersion
    $value | Add-Member -NotePropertyName releaseSequence -NotePropertyValue $ReleaseSequence
    if ($Tag -and $Tag -ne "v$($value.version)") { throw 'Git tag differs from product version' }
    if ($value.productId -ne 'usb-web-launcher' -or $value.channel -ne 'stable') { throw 'Invalid product identity or channel' }
    $id = [guid]$value.installerAppId
    $value | Add-Member -NotePropertyName fileVersion -NotePropertyValue "$($value.version).0"
    return $value
}
function Get-PayloadFiles([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory)
    return @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object {
        $_.FullName.Substring($root.Length).TrimStart('\','/') -ne 'payload-manifest.json'
    })
}
function Assert-SupportedSatelliteResources(
    [string]$Directory,
    [string[]]$SupportedLanguages = @('zh-Hans'),
    [switch]$RequireSupportedLanguage
) {
    $root = [IO.Path]::GetFullPath($Directory)
    $resourceFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.resources.dll' -Force)
    $unsupported = @($resourceFiles | Where-Object {
        $_.Directory.Name -notin $SupportedLanguages
    } | ForEach-Object {
        $_.FullName.Substring($root.Length).TrimStart('\','/')
    })
    if ($unsupported.Count) {
        throw "Payload contains unsupported satellite resources: $($unsupported -join ', ')"
    }
    if ($RequireSupportedLanguage) {
        foreach ($language in $SupportedLanguages) {
            if (-not ($resourceFiles | Where-Object { $_.Directory.Name -eq $language } | Select-Object -First 1)) {
                throw "Required satellite resources missing: $language"
            }
        }
    }
}
function Get-PayloadFootprint([string]$Directory) {
    $files = @(Get-PayloadFiles $Directory)
    $bytes = [long]0
    foreach ($file in $files) { $bytes += [long]$file.Length }
    return [pscustomobject]@{
        fileCount = [long]$files.Count
        unpackedBytes = $bytes
    }
}
function Get-PayloadFootprintPolicy([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Payload footprint policy missing: $Path"
    }
    $policy = [IO.File]::ReadAllText([IO.Path]::GetFullPath($Path)) | ConvertFrom-Json
    if ($policy.schemaVersion -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$policy.layout) -or
        [string]::IsNullOrWhiteSpace([string]$policy.measurement) -or
        [string]::IsNullOrWhiteSpace([string]$policy.rationale) -or
        [long]$policy.baseline.fileCount -lt 0 -or
        [long]$policy.baseline.unpackedBytes -lt 0 -or
        [long]$policy.maximum.fileCount -lt [long]$policy.baseline.fileCount -or
        [long]$policy.maximum.unpackedBytes -lt [long]$policy.baseline.unpackedBytes) {
        throw 'Invalid payload footprint policy'
    }
    return $policy
}
function Assert-PayloadFootprint($Footprint, $Policy) {
    $countDelta = [long]$Footprint.fileCount - [long]$Policy.maximum.fileCount
    $byteDelta = [long]$Footprint.unpackedBytes - [long]$Policy.maximum.unpackedBytes
    $failures = @()
    if ($countDelta -gt 0) {
        $failures += "file count exceeds maximum by $countDelta (actual $($Footprint.fileCount), maximum $($Policy.maximum.fileCount))"
    }
    if ($byteDelta -gt 0) {
        $failures += "unpacked bytes exceed maximum by $byteDelta (actual $($Footprint.unpackedBytes), maximum $($Policy.maximum.unpackedBytes))"
    }
    if ($failures.Count) {
        throw "Payload footprint exceeds committed bounds: $($failures -join '; ')"
    }
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
    Assert-SupportedSatelliteResources $root -RequireSupportedLanguage:(-not $SkipRequiredFiles)
    if (-not $SkipRequiredFiles) {
        foreach ($required in @(
            'UsbWebLauncher.App.exe',
            'UsbWebLauncher.App.dll',
            'UsbWebLauncher.Core.dll',
            'UsbWebLauncher.Windows.dll',
            'Microsoft.Web.WebView2.Core.dll',
            'Microsoft.Web.WebView2.Wpf.dll',
            'WebView2Loader.dll',
            'launcher-profile.json',
            'browser-extensions/ccb-ukey/manifest.json',
            'browser-extensions/ccb-ukey/background.js',
            'browser-extensions/ccb-ukey/content.js',
            'browser-extensions/ccb-ukey/_locales/zh_CN/messages.json',
            'broker/UsbWebLauncher.NativePromptBroker.exe',
            'broker/native-prompt-policy.json',
            'installer/Invoke-Install.ps1',
            'installer/InstallDirectory.psm1',
            'installer/Transaction.psm1',
            'installer/product.json'
        )) {
            if (-not (Test-Path -LiteralPath (Join-Path $root $required) -PathType Leaf)) { throw "Required payload file missing: $required" }
        }
        $policy = [IO.File]::ReadAllText((Join-Path $root 'broker/native-prompt-policy.json')) | ConvertFrom-Json
        if ($policy.Prompt.ProcessPath -or $policy.Prompt.ProcessSha256) { throw 'Broker policy must be unbound in release payload' }
    }
    Write-Host "Payload validation passed ($(@($files | Where-Object { -not $_.PSIsContainer }).Count) files)."
}
function Write-PayloadManifest(
    [string]$Directory,
    [string]$FootprintPolicyPath = (Join-Path $PSScriptRoot 'payload-footprint.json')
) {
    $root = [IO.Path]::GetFullPath($Directory)
    $files = @(Get-PayloadFiles $root)
    $entries = @($files | Sort-Object FullName | ForEach-Object {
        @{ path = $_.FullName.Substring($root.Length).TrimStart('\','/').Replace('\','/'); size = $_.Length; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $footprint = Get-PayloadFootprint $root
    $policy = Get-PayloadFootprintPolicy $FootprintPolicyPath
    Assert-PayloadFootprint $footprint $policy
    [ordered]@{
        schemaVersion = 2
        layout = $policy.layout
        footprint = [ordered]@{
            fileCount = $footprint.fileCount
            unpackedBytes = $footprint.unpackedBytes
            baselineFileCount = [long]$policy.baseline.fileCount
            baselineUnpackedBytes = [long]$policy.baseline.unpackedBytes
            maximumFileCount = [long]$policy.maximum.fileCount
            maximumUnpackedBytes = [long]$policy.maximum.unpackedBytes
        }
        files = $entries
    } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'payload-manifest.json') -Encoding UTF8
}
function Remove-PublishedSdkDocumentation([string]$Directory) {
    # These SDK IntelliSense documents are not runtime assets and contain vendor
    # sample machine paths. Remove only these exact root files, never XML configs.
    foreach ($name in @('Microsoft.Web.WebView2.Core.xml','Microsoft.Web.WebView2.WinForms.xml','Microsoft.Web.WebView2.Wpf.xml')) {
        $path = Join-Path ([IO.Path]::GetFullPath($Directory)) $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
    }
}
Export-ModuleMember -Function Get-ProductVersion,Assert-SupportedSatelliteResources,Get-PayloadFootprint,Get-PayloadFootprintPolicy,Assert-PayloadFootprint,Assert-CleanPayload,Write-PayloadManifest,Remove-PublishedSdkDocumentation
