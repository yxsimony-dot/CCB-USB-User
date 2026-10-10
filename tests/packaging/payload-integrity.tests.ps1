$ErrorActionPreference = 'Stop'
# Load only the pure validator; the installer entry point must never execute here.
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../../installer/Invoke-Install.ps1'),[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count) { throw 'Installer script contains syntax errors' }
$function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-Payload' },$true)
. ([scriptblock]::Create($function.Extent.Text))
function Reject([scriptblock]$action) { try { & $action } catch { return }; throw 'FAIL: altered payload accepted' }
$dir = Join-Path ([IO.Path]::GetTempPath()) ('uwl-integrity-' + [guid]::NewGuid())
New-Item $dir -ItemType Directory | Out-Null
try {
    Set-Content "$dir/app.txt" 'original'
    @{ files = @(@{path='app.txt';size=(Get-Item "$dir/app.txt").Length;sha256=(Get-FileHash "$dir/app.txt").Hash}) } | ConvertTo-Json -Depth 4 | Set-Content "$dir/payload-manifest.json"
    Assert-Payload $dir
    Set-Content "$dir/unlisted.txt" 'unexpected'
    Reject { Assert-Payload $dir }
    Remove-Item "$dir/unlisted.txt"
    Set-Content "$dir/app.txt" 'tampered'
    Reject { Assert-Payload $dir }
    Remove-Item "$dir/app.txt"
    Reject { Assert-Payload $dir }
    Set-Content "$dir/payload-manifest.json" '{"files":[{"path":"../escape.txt","size":1,"sha256":"none"}]}'
    Reject { Assert-Payload $dir }
    Write-Host 'PASS: exact file set, altered/missing payload and path traversal rejection'
} finally { Remove-Item -LiteralPath $dir -Recurse -Force }
