$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot '../../build/Packaging.psm1'
if (-not (Test-Path $module)) { throw 'FAIL: version and payload validation missing' }
Import-Module $module -Force
function Assert($condition,$message) { if (-not $condition) { throw "FAIL: $message" } }
function Reject([scriptblock]$action) { try { & $action } catch { return }; throw 'FAIL: invalid input accepted' }
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
    Set-Content "$dir/application.xml" '<configuration>C:\private-machine-path</configuration>'
    Reject { Assert-CleanPayload $dir -SkipRequiredFiles }
    Write-Host 'PASS: version derivation, tag mismatch, unstable version, PDF, developer path and debugging payload rejection'
} finally { Remove-Item -LiteralPath $dir -Recurse -Force }
