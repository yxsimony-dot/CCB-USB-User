$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot '../../installer/Transaction.psm1'
if (-not (Test-Path $module)) { throw 'FAIL: transaction module missing; rollback behavior is not implemented' }
Import-Module $module -Force
function Assert($condition, $message) { if (-not $condition) { throw "FAIL: $message" } }
function ExpectFailure([scriptblock]$action, [string]$pattern) {
    try { & $action } catch { Assert ($_.Exception.Message -match $pattern) "Expected $pattern, got $($_.Exception.Message)"; return }
    throw "FAIL: expected failure $pattern"
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('uwl-test-' + [guid]::NewGuid())
New-Item $root -ItemType Directory | Out-Null
try {
    $app = Join-Path $root 'custom app 名称'; $payload = Join-Path $root 'payload'
    New-Item $app,$payload -ItemType Directory | Out-Null
    Set-Content (Join-Path $app 'app.txt') 'old'
    Set-Content (Join-Path $app 'installed-version.json') '{"version":"1.0.0"}'
    Set-Content (Join-Path $app 'payload-manifest.json') '{"files":[{"path":"app.txt"}]}'
    Set-Content (Join-Path $payload 'app.txt') 'new'
    Set-Content (Join-Path $payload 'payload-manifest.json') '{"files":[{"path":"app.txt"}]}'
    Set-Content (Join-Path $root 'user-data.pdf') 'preserved'
    $noop = { param($path) }
    Set-Content "$app/user.pdf" 'keep-this'
    ExpectFailure { Invoke-InstallTransaction $app $payload '1.1.0' $noop $noop $noop } 'Unowned'
    Assert ((Get-Content "$app/user.pdf") -eq 'keep-this') 'unowned PDF lost'
    Remove-Item "$app/user.pdf"
    $backupFail = { param($path) throw 'snapshot unavailable' }
    $shouldNotRestore = { param($path) throw 'restore called unexpectedly' }
    ExpectFailure { Invoke-InstallTransaction $app $payload '1.1.0' $noop $noop $noop $backupFail $shouldNotRestore } 'snapshot unavailable'
    Assert (-not (Test-Path "$app.transaction")) 'failed snapshot left an app transaction'
    Assert ((Get-Content "$app/app.txt") -eq 'old') 'failed snapshot changed app'
    ExpectFailure { Invoke-InstallTransaction $app $payload '0.9.0' $noop $noop $noop } 'Downgrade'
    Assert ((Get-Content (Join-Path $app 'app.txt')) -eq 'old') 'downgrade changed old files'
    $failNew = { param($path) if ((Get-Content (Join-Path $path 'app.txt')) -eq 'new') { throw 'injected health failure' } }
    ExpectFailure { Invoke-InstallTransaction $app $payload '1.1.0' $noop $noop $failNew } 'Previous version restored'
    Assert ((Get-Content (Join-Path $app 'app.txt')) -eq 'old') 'health failure did not restore old files'
    Assert ((Get-Content (Join-Path $app 'installed-version.json') | ConvertFrom-Json).version -eq '1.0.0') 'old version not restored'
    Invoke-InstallTransaction $app $payload '1.0.0' $noop $noop $noop
    Assert ((Get-Content (Join-Path $app 'app.txt')) -eq 'new') 'same version repair failed'
    Assert ((Get-Content (Join-Path $root 'user-data.pdf')) -eq 'preserved') 'user data changed'
    $alwaysFail = { param($path) throw 'injected broker failure' }
    ExpectFailure { Invoke-InstallTransaction $app $payload '1.2.0' $noop $alwaysFail $noop } 'Recovery required'
    Assert (Test-Path "$app.transaction/backup/app.txt") 'recovery backup was removed'
    Assert (Test-Path "$app.transaction/state.json") 'recovery state was removed'
    Restore-InstallTransaction $app $noop $noop
    Assert (-not (Test-Path "$app.transaction")) 'explicit recovery did not finish'
    # A broker handshake failure on first install must restore even a legacy broker,
    # without requiring a prior application payload to exist.
    $freshApp = Join-Path $root 'fresh-app'
    $marker = Join-Path $root 'broker-state.txt'
    Set-Content $marker 'legacy-broker'
    $backupBroker = { param($path) Copy-Item $marker "$marker.backup" }
    $replaceBroker = { param($path) Set-Content $marker 'new-broker' }
    $restoreBroker = { param($path) Copy-Item "$marker.backup" $marker -Force }
    ExpectFailure { Invoke-InstallTransaction $freshApp $payload '1.0.0' $noop $replaceBroker $alwaysFail $backupBroker $restoreBroker } 'Previous version restored'
    Assert (-not (Test-Path $freshApp)) 'failed first installation left an application'
    Assert ((Get-Content $marker) -eq 'legacy-broker') 'legacy broker was not restored'
    # Registry/uninstaller creation belongs outside the filesystem transaction.
    # Verify the rollback backup survives until the outer installer commits.
    Invoke-InstallTransaction $app $payload '1.1.0' $noop $noop $noop -DeferCommit
    Assert (Test-Path "$app.transaction/backup") 'backup removed before outer commit'
    ExpectFailure { Invoke-InstallTransaction $app $payload '1.1.0' $noop $noop $noop } 'unfinished transaction'
    Complete-InstallTransaction $app $noop
    Assert (-not (Test-Path "$app.transaction")) 'commit did not remove transaction'
    Set-Content "$payload/app.txt" 'future'
    Invoke-InstallTransaction $app $payload '1.2.0' $noop $noop $noop -DeferCommit
    $commitInterrupted = { param($path) Set-Content $marker 'snapshot-deleted'; throw 'commit acknowledgement interrupted' }
    ExpectFailure { Complete-InstallTransaction $app $commitInterrupted } 'commit acknowledgement interrupted'
    Assert ((Get-Content "$app.transaction/state.json" -Raw | ConvertFrom-Json).phase -eq 'committing') 'commit direction was not persisted before deleting broker snapshot'
    $futureHealth = { param($path) Assert ((Get-Content "$path/app.txt") -eq 'future') 'commit recovery rolled back the verified version' }
    Restore-InstallTransaction $app $noop $futureHealth $shouldNotRestore $noop
    Assert ((Get-Content "$app/app.txt") -eq 'future') 'interrupted commit did not finish forward'
    Assert (-not (Test-Path "$app.transaction")) 'interrupted commit cleanup incomplete'
    Invoke-InstallTransaction $app $payload '1.2.0' $noop $noop $noop -DeferCommit
    $lock = [IO.File]::Open("$app.transaction/cleanup.lock",[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { ExpectFailure { Complete-InstallTransaction $app $noop } '.' } finally { $lock.Dispose() }
    Assert ((Get-Content "$app.transaction/state.json" -Raw | ConvertFrom-Json).phase -eq 'committed') 'cleanup failure erased the forward recovery marker'
    Restore-InstallTransaction $app $noop $futureHealth $shouldNotRestore $noop
    Assert (-not (Test-Path "$app.transaction")) 'cleanup failure could not be retried'
    Set-Content "$payload/app.txt" 'bad-version'
    $badHealth = { param($path) if ((Get-Content "$path/app.txt") -eq 'bad-version') { throw 'new version unhealthy' } }
    ExpectFailure { Invoke-InstallTransaction $app $payload '1.3.0' $noop $noop $badHealth $noop $noop -FinishRecovery $commitInterrupted } 'Recovery required'
    Assert ((Get-Content "$app.transaction/state.json" -Raw | ConvertFrom-Json).phase -eq 'restored') 'restored direction not persisted before snapshot cleanup'
    Restore-InstallTransaction $app $noop $futureHealth $shouldNotRestore $noop
    Assert ((Get-Content "$app/app.txt") -eq 'future') 'rollback-finalize retry replaced restored version'
    New-Item "$app.transaction/staged" -ItemType Directory -Force | Out-Null
    Set-Content "$app.transaction/staged/partial.txt" 'interrupted copy'
    Set-Content "$app.transaction/state.json" '{"phase":"staging","hadPrevious":true,"brokerBackupAcknowledged":false}'
    Restore-InstallTransaction $app $noop $futureHealth $shouldNotRestore $shouldNotRestore
    Assert (-not (Test-Path "$app.transaction")) 'interrupted staging not cleared'
    New-Item "$app.transaction/staged" -ItemType Directory -Force | Out-Null
    Set-Content "$app.transaction/state.json" '{"phase":"snapshot-started","hadPrevious":true,"brokerBackupAcknowledged":false}'
    # The process may have died before invoking the read-only backup. Neither
    # broker restoration nor registry import is valid when no snapshot exists.
    Restore-InstallTransaction $app $noop $futureHealth $shouldNotRestore $noop
    Assert (-not (Test-Path "$app.transaction")) 'snapshot-started without a broker snapshot not recoverable'
    Assert ((Get-Content "$app/app.txt") -eq 'future') 'snapshot-only interruption changed installed app'
    New-Item "$app.transaction/staged" -ItemType Directory -Force | Out-Null
    Set-Content "$app.transaction/state.json" '{"phase":"snapshot-started","hadPrevious":true,"brokerBackupAcknowledged":false}'
    ExpectFailure { Restore-InstallTransaction $app $noop $alwaysFail $shouldNotRestore $commitInterrupted } 'commit acknowledgement interrupted'
    Restore-InstallTransaction $app $noop $alwaysFail $shouldNotRestore $noop
    Assert (-not (Test-Path "$app.transaction")) 'snapshot-only cleanup retry incorrectly required old broker health'
    New-Item "$app.transaction/staged" -ItemType Directory -Force | Out-Null
    Set-Content "$app.transaction/staged/partial.txt" 'legacy interrupted copy with no metadata'
    Restore-InstallTransaction $app $noop $futureHealth $shouldNotRestore $shouldNotRestore
    Assert (-not (Test-Path "$app.transaction")) 'safe legacy staging-only transaction not cleared'
    New-Item "$app.transaction/staged" -ItemType Directory -Force | Out-Null
    Set-Content "$app.transaction/state.next.json" '{"phase":'
    Restore-InstallTransaction $app $noop $futureHealth $shouldNotRestore $shouldNotRestore
    Assert (-not (Test-Path "$app.transaction")) 'interrupted first metadata write blocked safe staging recovery'
    Set-Content "$payload/app.txt" 'future'
    $validateStagingState = { param($path) Assert ((Get-Content "$app.transaction/state.json" -Raw | ConvertFrom-Json).phase -eq 'staging') 'staging metadata missing before payload verification' }
    Invoke-InstallTransaction $app $payload '1.2.0' $noop $noop $noop -ValidateStaged $validateStagingState
    Write-Host 'PASS: downgrade, repair, data protection, rollback, first-install recovery, commit interruption, locked cleanup retry, restored finalization and interrupted staging'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
