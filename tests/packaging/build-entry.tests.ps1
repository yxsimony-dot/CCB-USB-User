$ErrorActionPreference = 'Stop'
function Assert($condition,$message) { if (-not $condition) { throw "FAIL: $message" } }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('uwl-build-gates-' + [guid]::NewGuid())
New-Item "$fixture/build" -ItemType Directory -Force | Out-Null
Copy-Item "$PSScriptRoot/../../build/*" "$fixture/build"
Set-Content "$fixture/.gitignore" 'artifacts/'
Set-Content "$fixture/compiler.ps1" "Write-Output 'Compiler engine version: Inno Setup 6.7.3'"
$previousActions = $env:GITHUB_ACTIONS
$env:GITHUB_ACTIONS = 'false'
function global:dotnet {
    if ($args[0] -eq '--version') { '10.0.100'; $global:LASTEXITCODE=0; return }
    if ($args[0] -eq 'test') { $global:LASTEXITCODE=17; return }
    if ($args[0] -eq 'publish') { throw 'FAIL: publish reached after test failure' }
    $global:LASTEXITCODE=0
}
function global:npm.cmd { $global:LASTEXITCODE=0 }
function ExpectBuildFailure([string]$Expected, [string]$Compiler = "$fixture/compiler.ps1") {
    $caught = $null
    try { & "$fixture/build/build.ps1" -IsccPath $Compiler -ReleaseSequence 10 } catch { $caught = $_.Exception.Message }
    Assert ($caught -like "*$Expected*") "Expected '$Expected', found '$caught'"
    Assert (-not (Test-Path "$fixture/artifacts/releases/1.1.10/release-manifest.json")) 'failed build emitted release metadata'
}
Push-Location $fixture
try {
    & git init --quiet
    & git add .
    & git -c user.name='Build test' -c user.email='build-test@example.invalid' commit --quiet -m fixture
    Assert ($LASTEXITCODE -eq 0) 'fixture commit created'
    ExpectBuildFailure 'ISCC.exe is required' "$fixture/missing.exe"
    & git tag v9.0.0
    ExpectBuildFailure 'Git tag differs'
    & git tag -d v9.0.0 | Out-Null
    Set-Content "$fixture/tracked-later.txt" 'dirty source'
    ExpectBuildFailure 'clean Git checkout'
    Remove-Item "$fixture/tracked-later.txt"
    ExpectBuildFailure 'dotnet failed with exit code 17'
    Write-Host 'PASS: missing tool, tag mismatch, dirty source and failed tests stop build before publishing'
} finally {
    Pop-Location
    Remove-Item Function:/dotnet,Function:/npm.cmd
    $env:GITHUB_ACTIONS = $previousActions
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
