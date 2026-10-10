$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../../build/Packaging.psm1') -Force
function Assert($condition,$message) { if (-not $condition) { throw "FAIL: $message" } }
function Reject([scriptblock]$action) { try { & $action | Out-Null } catch { return }; throw 'FAIL: invalid version accepted' }
$source = Join-Path $PSScriptRoot '../../build/version.json'
$version = Get-ProductVersion $source '' -ReleaseSequence 10
Assert ($version.version -eq '1.1.10') 'run number must produce effective 1.1.10'
Assert ($version.baseVersion -eq '1.1.0') 'base version retained'
Assert ($version.fileVersion -eq '1.1.10.0') 'Windows file version derived'
Assert ((Get-ProductVersion $source 'v1.1.10' -ReleaseSequence 10).version -eq $version.version) 'same run is stable'
Assert ([version](Get-ProductVersion $source '' -ReleaseSequence 10).version -gt [version](Get-ProductVersion $source '' -ReleaseSequence 9).version) 'numeric order'
Assert ((Get-ProductVersion $source '').version -eq '1.1.0') 'local uses base version'
Assert ((Get-ProductVersion $source '' -ReleaseSequence 65535).version -eq '1.1.65535') 'maximum Windows patch accepted'
Reject { Get-ProductVersion $source '' -ReleaseSequence 65536 }
Reject { Get-ProductVersion $source '' -ReleaseSequence 0 }
Reject { Get-ProductVersion $source '' -ReleaseSequence -1 }
Reject { Get-ProductVersion $source 'v1.1.9' -ReleaseSequence 10 }
Write-Host 'PASS: effective versions, retry stability, numeric order and Windows bounds'
$sourceHash = (Get-FileHash $source).Hash
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('uwl-version-' + [guid]::NewGuid() + '.json')
try {
    $base = Get-Content $source -Raw | ConvertFrom-Json
    $base.version = '2.3.7'
    $base | ConvertTo-Json | Set-Content $fixture -Encoding utf8
    Assert ((Get-ProductVersion $fixture '' -ReleaseSequence 9).version -eq '2.3.16') 'base patch contributes to effective version'
    $base.version = '65535.65535.65534'
    $base | ConvertTo-Json | Set-Content $fixture -Encoding utf8
    Assert ((Get-ProductVersion $fixture '' -ReleaseSequence 1).version -eq '65535.65535.65535') 'all Windows parts support boundary'
    Reject { Get-ProductVersion $fixture '' -ReleaseSequence 2 }
    foreach ($invalid in @('65536.0.0','1.65536.0','1.1.65536','01.1.0','1.1.0-beta')) {
        $base.version = $invalid
        $base | ConvertTo-Json | Set-Content $fixture -Encoding utf8
        Reject { Get-ProductVersion $fixture '' }
    }
    Assert ((Get-FileHash $source).Hash -eq $sourceHash) 'version resolution leaves source unchanged'
} finally { Remove-Item -LiteralPath $fixture -Force }
Write-Host 'PASS: base patch addition, all part boundaries and immutable version source'
