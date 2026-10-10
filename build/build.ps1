#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$IsccPath,
    [string]$OutputDirectory,
    [string]$SigningCertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'Packaging.psm1') -Force
function Run([string]$File,[string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}
Push-Location $repo
try {
    if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw 'Inno Setup 6.7.3 ISCC.exe is required. Supply -IsccPath.' }
    # ISCC's PE file version can be 0.0.0.0. Read its actual compiler-engine banner.
    # The definition deliberately refuses compilation without ProductVersion.
    $compilerProbe = (& $IsccPath "$repo/installer/UsbWebLauncher.iss" 2>&1) -join "`n"
    if ($compilerProbe -notmatch 'Compiler engine version: Inno Setup (\d+\.\d+\.\d+)') { throw 'Cannot determine Inno Setup compiler version' }
    $compilerVersion = $Matches[1]
    if ($compilerVersion -ne '6.7.3') { throw "Expected Inno Setup 6.7.3, found $compilerVersion" }
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -ne '10.0.100') { throw '.NET SDK 10.0.100 is required by global.json' }
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'A Git checkout with source commit is required' }
    $tags = @(& git tag --points-at HEAD)
    foreach ($tag in $tags) { if ($tag -match '^v\d') { $null = Get-ProductVersion "$PSScriptRoot/version.json" $tag } }
    $version = Get-ProductVersion "$PSScriptRoot/version.json" ''
    $dirty = [bool](& git status --porcelain)
    $buildRoot = Join-Path $repo ('artifacts/installer-build/' + [guid]::NewGuid())
    $payload = Join-Path $buildRoot 'payload'
    $release = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repo "artifacts/releases/$($version.version)" }
    if ((Test-Path $release) -and @(Get-ChildItem $release -Force).Count) { throw 'OutputDirectory must be empty; old output cannot be mistaken for this build' }
    New-Item -ItemType Directory -Path $payload,$release -Force | Out-Null
    Run 'dotnet' @('restore','UsbWebLauncher.sln','--locked-mode')
    Run 'npm.cmd' @('ci','--ignore-scripts','--no-audit','--no-fund')
    Run 'dotnet' @('test','UsbWebLauncher.sln','-c','Release','--no-restore','--filter','Category!=Hardware')
    Run 'npm.cmd' @('test')
    Run 'powershell.exe' @('-NoProfile','-File','tests/packaging/transaction.tests.ps1')
    Run 'pwsh' @('-NoProfile','-File','tests/packaging/build-contract.tests.ps1')
    Run 'powershell.exe' @('-NoProfile','-File','tests/packaging/payload-integrity.tests.ps1')
    # Each publish restores only from committed locks, into a completely new directory.
    Run 'dotnet' @('publish','src/UsbWebLauncher.App','-c','Release','-r','win-x64','--self-contained','true','-p:RestoreLockedMode=true',"-p:SourceRevisionId=$commit",'-p:DebugType=None','-p:DebugSymbols=false','-o',$payload)
    Run 'dotnet' @('publish','src/UsbWebLauncher.NativePromptBroker','-c','Release','-r','win-x64','--self-contained','true','-p:RestoreLockedMode=true',"-p:SourceRevisionId=$commit",'-p:DebugType=None','-p:DebugSymbols=false','-o',"$payload/broker")
    New-Item "$payload/installer" -ItemType Directory | Out-Null
    Copy-Item "$repo/installer/*.ps1","$repo/installer/*.psm1" "$payload/installer"
    Copy-Item "$PSScriptRoot/version.json" "$payload/installer/product.json"
    if ($SigningCertificateThumbprint) {
        $null = Get-Command signtool.exe -ErrorAction Stop
        foreach ($file in @("$payload/UsbWebLauncher.App.exe","$payload/broker/UsbWebLauncher.NativePromptBroker.exe")) {
            Run 'signtool.exe' @('sign','/sha1',$SigningCertificateThumbprint,'/fd','SHA256','/tr',$TimestampUrl,'/td','SHA256',$file)
        }
    }
    Assert-CleanPayload $payload
    Write-PayloadManifest $payload
    Run $IsccPath @("/DProductVersion=$($version.version)","/DFileVersion=$($version.fileVersion)","/DInstallerAppId=$($version.installerAppId)","/DPayloadDirectory=$payload","/DReleaseDirectory=$release","$repo/installer/UsbWebLauncher.iss")
    $installerName = "UsbWebLauncher-$($version.version)-win-x64-setup.exe"
    $installer = Join-Path $release $installerName
    if ($SigningCertificateThumbprint) { Run 'signtool.exe' @('sign','/sha1',$SigningCertificateThumbprint,'/fd','SHA256','/tr',$TimestampUrl,'/td','SHA256',$installer) }
    $hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $installerName" | Set-Content "$release/SHA256SUMS" -Encoding ascii
    $manifest = [ordered]@{
        schemaVersion=1; productId=$version.productId; version=$version.version; channel=$version.channel
        platform='windows'; architecture='x64'; minimumOsVersion='10.0.19041'
        sourceRepository=$version.sourceRepository; sourceCommit=$commit; sourceDirty=$dirty
        installerFileName=$installerName; size=(Get-Item $installer).Length; sha256=$hash
        brokerProtocolVersion=$version.brokerProtocolVersion; settingsSchemaVersion=$version.settingsSchemaVersion
        signatureStatus=$(if ($SigningCertificateThumbprint) { 'signed' } else { 'unsigned' })
        tools=@{ dotnetSdk=$sdk; innoSetup=$compilerVersion; node=(& node --version) }
        validation=@{ automatedTests='passed'; payloadValidation='passed'; cleanMachineAcceptance='not-performed' }
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content "$release/release-manifest.json" -Encoding utf8
    Write-Host "Installer and metadata: $release"
} finally { Pop-Location }
