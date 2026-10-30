$ErrorActionPreference = 'Stop'
function Assert($condition,$message) { if (-not $condition) { throw "FAIL: $message" } }
$installerScript = Get-Content (Join-Path $PSScriptRoot '../../installer/UsbWebLauncher.iss') -Raw
Assert ($installerScript -match '(?m)^Name:\s*"\{userdesktop\}\\UsbWebLauncher";\s*Filename:\s*"\{app\}\\UsbWebLauncher\.App\.exe"') 'desktop shortcut definition'
Assert ($installerScript -match '(?m)^Name:\s*"\{userprograms\}\\UsbWebLauncher";\s*Filename:\s*"\{app\}\\UsbWebLauncher\.App\.exe"') 'start menu shortcut definition'
Write-Host 'PASS: desktop and start menu shortcuts are defined'

$module = Join-Path $PSScriptRoot '../../build/Packaging.psm1'
if (-not (Test-Path $module)) { throw 'FAIL: version and payload validation missing' }
Import-Module $module -Force
function Reject([scriptblock]$action) { try { & $action } catch { return }; throw 'FAIL: invalid input accepted' }
function CaptureFailure([scriptblock]$action) { try { & $action } catch { return $_.Exception.Message }; throw 'FAIL: invalid input accepted' }
$props = [xml](Get-Content (Join-Path $PSScriptRoot '../../Directory.Build.props') -Raw)
$commonProperties = $props.Project.PropertyGroup
Assert ($commonProperties.RuntimeIdentifiers -eq 'win-x64') 'common runtime identifier'
Assert ($commonProperties.SatelliteResourceLanguages -eq 'zh-Hans') 'supported satellite language'
Assert ($commonProperties.PublishTrimmed -eq 'false') 'trimming must remain disabled'
$appProject = [xml](Get-Content (Join-Path $PSScriptRoot '../../src/UsbWebLauncher.App/UsbWebLauncher.App.csproj') -Raw)
$appProperties = $appProject.Project.PropertyGroup
Assert ($appProperties.SelfContained -eq 'true') 'App must remain self-contained'
Assert ($appProperties.PublishSingleFile -eq 'false') 'production App layout must remain conventional until candidate acceptance'
Assert ($appProperties.PublishTrimmed -eq 'false') 'App trimming must remain disabled'
$brokerProject = [xml](Get-Content (Join-Path $PSScriptRoot '../../src/UsbWebLauncher.NativePromptBroker/UsbWebLauncher.NativePromptBroker.csproj') -Raw)
$brokerProperties = $brokerProject.Project.PropertyGroup
Assert ($brokerProperties.SelfContained -eq 'true') 'broker must remain self-contained'
Assert ($brokerProperties.PublishSingleFile -eq 'true') 'broker single-file contract'
Assert ($brokerProperties.EnableSingleFileAnalyzer -eq 'false') 'broker publish must not add trim tooling to shared project locks'
$buildEntry = Get-Content (Join-Path $PSScriptRoot '../../build/build.ps1') -Raw
Assert ($buildEntry -match "'-p:PublishSingleFile=false','-p:PublishTrimmed=false'") 'App publish safety properties'
Assert ($buildEntry -match "'-p:PublishSingleFile=true','-p:PublishTrimmed=false','-p:EnableSingleFileAnalyzer=false'") 'broker publish safety properties'
$version = Get-ProductVersion (Join-Path $PSScriptRoot '../../build/version.json') ''
Assert ($version.fileVersion -eq '1.1.0.0') 'file version derivation'
Reject { Get-ProductVersion (Join-Path $PSScriptRoot '../../build/version.json') 'v9.0.0' }
$dir = Join-Path ([IO.Path]::GetTempPath()) ('uwl-payload-test-' + [guid]::NewGuid())
New-Item $dir -ItemType Directory | Out-Null
try {
    Set-Content "$dir/metadata.json" '{"version":"1.0.0"}'
    Assert-CleanPayload $dir -SkipRequiredFiles
    Set-Content "$dir/bank.pdf" 'synthetic'
    Reject { Assert-CleanPayload $dir -SkipRequiredFiles }
    Remove-Item "$dir/bank.pdf"
    Set-Content "$dir/metadata.json" '{"path":"F:\\USB-Web-Launcher\\secret"}'
    Reject { Assert-CleanPayload $dir -SkipRequiredFiles }
    Set-Content "$dir/metadata.json" '{"debug":"--remote-debugging-port=9222"}'
    Reject { Assert-CleanPayload $dir -SkipRequiredFiles }
    foreach ($field in @('pin','secret','token','password')) {
        Set-Content "$dir/metadata.json" ('{"nested":{"' + $field + '":"synthetic-only"}}')
        Reject { Assert-CleanPayload $dir -SkipRequiredFiles }
    }
    Set-Content "$dir/version.json" '{"version":"1.0.0-beta"}'
    Reject { Get-ProductVersion "$dir/version.json" '' }
    foreach ($name in @('Microsoft.Web.WebView2.Core.xml','Microsoft.Web.WebView2.WinForms.xml','Microsoft.Web.WebView2.Wpf.xml')) {
        Set-Content (Join-Path $dir $name) '<doc>Vendor example: C:\example</doc>'
    }
    Set-Content "$dir/application.xml" '<configuration>Keep this runtime configuration</configuration>'
    Assert ([bool](Get-Command Remove-PublishedSdkDocumentation -ErrorAction SilentlyContinue)) 'explicit SDK documentation pruning is missing'
    Remove-PublishedSdkDocumentation $dir
    Assert (-not (Test-Path "$dir/Microsoft.Web.WebView2.Core.xml")) 'SDK Core documentation retained'
    Assert (-not (Test-Path "$dir/Microsoft.Web.WebView2.WinForms.xml")) 'SDK WinForms documentation retained'
    Assert (-not (Test-Path "$dir/Microsoft.Web.WebView2.Wpf.xml")) 'SDK WPF documentation retained'
    Assert (Test-Path "$dir/application.xml") 'runtime XML configuration was removed'
    Set-Content "$dir/metadata.json" '{}'
    Assert-CleanPayload $dir -SkipRequiredFiles
    New-Item "$dir/zh-Hans" -ItemType Directory | Out-Null
    Set-Content "$dir/zh-Hans/example.resources.dll" 'supported'
    Assert-CleanPayload $dir -SkipRequiredFiles
    New-Item "$dir/fr" -ItemType Directory | Out-Null
    Set-Content "$dir/fr/example.resources.dll" 'unsupported'
    Reject { Assert-CleanPayload $dir -SkipRequiredFiles }
    Remove-Item "$dir/fr" -Recurse -Force
    Set-Content "$dir/application.xml" '<configuration>C:\private-machine-path</configuration>'
    Reject { Assert-CleanPayload $dir -SkipRequiredFiles }
    Write-Host 'PASS: publish properties, culture filtering, version and payload content validation'
} finally { Remove-Item -LiteralPath $dir -Recurse -Force }

$footprintDir = Join-Path ([IO.Path]::GetTempPath()) ('uwl-footprint-test-' + [guid]::NewGuid())
$policyPath = Join-Path ([IO.Path]::GetTempPath()) ('uwl-footprint-policy-' + [guid]::NewGuid() + '.json')
New-Item $footprintDir -ItemType Directory | Out-Null
try {
    Set-Content "$footprintDir/app.txt" '12345' -NoNewline
    @{
        schemaVersion = 1
        layout = 'test'
        measurement = 'test files'
        baseline = @{ fileCount = 1; unpackedBytes = 5 }
        maximum = @{ fileCount = 1; unpackedBytes = 5 }
        rationale = 'Synthetic packaging contract.'
    } | ConvertTo-Json -Depth 4 | Set-Content $policyPath
    Write-PayloadManifest $footprintDir $policyPath
    $manifest = Get-Content "$footprintDir/payload-manifest.json" -Raw | ConvertFrom-Json
    Assert ($manifest.schemaVersion -eq 2) 'payload manifest schema'
    Assert ($manifest.footprint.fileCount -eq 1) 'payload file count'
    Assert ($manifest.footprint.unpackedBytes -eq 5) 'payload unpacked bytes'
    Set-Content "$footprintDir/extra.txt" 'x' -NoNewline
    $countFailure = CaptureFailure { Write-PayloadManifest $footprintDir $policyPath }
    Assert ($countFailure -match 'file count exceeds maximum by 1 \(actual 2, maximum 1\)') 'file-count delta'
    Remove-Item "$footprintDir/extra.txt"
    $policy = Get-Content $policyPath -Raw | ConvertFrom-Json
    $policy.baseline.unpackedBytes = 4
    $policy.maximum.fileCount = 2
    $policy.maximum.unpackedBytes = 4
    $policy | ConvertTo-Json -Depth 4 | Set-Content $policyPath
    $sizeFailure = CaptureFailure { Write-PayloadManifest $footprintDir $policyPath }
    Assert ($sizeFailure -match 'unpacked bytes exceed maximum by 1 \(actual 5, maximum 4\)') 'unpacked-byte delta'
    Write-Host 'PASS: payload manifest metrics and measured bound deltas'
} finally {
    Remove-Item -LiteralPath $footprintDir -Recurse -Force
    Remove-Item -LiteralPath $policyPath -Force -ErrorAction SilentlyContinue
}
