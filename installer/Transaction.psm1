Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-TransactionPath([string]$AppDirectory) {
    $full = [IO.Path]::GetFullPath($AppDirectory).TrimEnd('\','/')
    if ([IO.Path]::GetPathRoot($full).TrimEnd('\','/') -eq $full) { throw 'Installation root cannot be a drive root' }
    foreach ($directory in @($full, "$full.transaction")) {
        if (Test-Path -LiteralPath $directory) {
            $items = @(Get-Item -LiteralPath $directory) + @(Get-ChildItem -LiteralPath $directory -Recurse -Force)
            if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Reparse points are not allowed in installation paths' }
        }
    }
    return $full
}
function Remove-TransactionDirectory([string]$Path, [string]$AppDirectory) {
    $app = Assert-TransactionPath $AppDirectory
    $target = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    if ($target -ne $app -and $target -ne "$app.transaction" -and -not $target.StartsWith("$app.transaction\", [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe transaction cleanup path' }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}
function Write-TransactionState($Directory, $State) {
    $State | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Directory 'state.next.json') -Encoding UTF8
    Move-Item -LiteralPath (Join-Path $Directory 'state.next.json') -Destination (Join-Path $Directory 'state.json') -Force
}
function Remove-FinalizedTransaction([string]$AppDirectory) {
    $app = Assert-TransactionPath $AppDirectory; $txn = "$app.transaction"
    # Preserve the recovery direction until every potentially failing recursive
    # deletion has finished. A locked backup must never erase the state first.
    foreach ($item in Get-ChildItem -LiteralPath $txn -Force) {
        if ($item.Name -ne 'state.json') {
            if ($item.PSIsContainer) { Remove-TransactionDirectory $item.FullName $app }
            else { Remove-Item -LiteralPath $item.FullName -Force }
        }
    }
    if (Test-Path "$txn/state.json") { Remove-Item -LiteralPath "$txn/state.json" -Force }
    Remove-Item -LiteralPath $txn -Force
}
function Assert-OwnedInstallation([string]$AppDirectory) {
    if (-not (Test-Path $AppDirectory)) { return }
    $files = @(Get-ChildItem -LiteralPath $AppDirectory -Recurse -File -Force)
    if ($files.Count -eq 0) { return }
    if (-not (Test-Path "$AppDirectory/payload-manifest.json")) { throw 'Unowned installation directory: move its existing files elsewhere before installing' }
    $manifest = Get-Content "$AppDirectory/payload-manifest.json" -Raw | ConvertFrom-Json
    $owned = @($manifest.files | ForEach-Object { $_.path.Replace('/','\') }) + @('payload-manifest.json','installed-version.json')
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($AppDirectory.Length).TrimStart('\')
        if ($relative -notin $owned -and $relative -notmatch '^unins\d+\.(exe|dat|msg)$') { throw "Unowned file in application directory: $relative. Move it to your documents folder before upgrading." }
    }
}
function Restore-InstallTransaction {
    param([string]$AppDirectory, [scriptblock]$RegisterBroker, [scriptblock]$HealthCheck, [scriptblock]$RestoreBroker, [scriptblock]$FinishRecovery)
    $app = Assert-TransactionPath $AppDirectory; $txn = "$app.transaction"
    if (-not (Test-Path "$txn/state.json") -and (Test-Path "$txn/state.next.json")) {
        # State replacement interrupted between the temporary and final name.
        $validNext = $false
        try { $null = Get-Content "$txn/state.next.json" -Raw | ConvertFrom-Json; $validNext = $true } catch { }
        if ($validNext) { Move-Item -LiteralPath "$txn/state.next.json" -Destination "$txn/state.json" }
    }
    if (-not (Test-Path "$txn/state.json")) {
        $children = @(Get-ChildItem -LiteralPath $txn -Force)
        if (@($children | Where-Object { ($_.Name -ne 'staged' -or -not $_.PSIsContainer) -and ($_.Name -ne 'state.next.json' -or $_.PSIsContainer) }).Count) {
            throw 'Recovery required: metadata missing and non-staging files found. Preserve this directory for manual repair.'
        }
        # Empty / staging-only directories precede broker or application mutation.
        Remove-FinalizedTransaction $app
        return
    }
    $state = Get-Content "$txn/state.json" -Raw | ConvertFrom-Json
    if ($state.phase -in @('committing','committed')) {
        & $HealthCheck $app
        Complete-InstallTransaction $app $FinishRecovery
        return
    }
    if ($state.phase -eq 'restored') {
        if (Test-Path "$app/installed-version.json") { & $HealthCheck $app }
        if ($FinishRecovery) { & $FinishRecovery $app }
        Remove-FinalizedTransaction $app
        return
    }
    if ($state.phase -eq 'staging') {
        Remove-FinalizedTransaction $app
        return
    }
    if ($state.phase -eq 'prepared' -and -not $state.brokerBackupAcknowledged -and -not (Test-Path "$txn/backup")) {
        # Interrupted before any external mutation: staging alone can be discarded.
        Remove-FinalizedTransaction $app
        return
    }
    if ($state.phase -eq 'snapshot-started') {
        # Backup is read-only: a crash here may precede both registry export and
        # broker snapshot creation. Active files/task/registration are unchanged.
        # The ownership-checked idempotent commit discards any partial snapshot.
        # Keep snapshot-started through cleanup: retry must not demand the old
        # broker is healthy when this attempt has not changed active state.
        if ($FinishRecovery) { & $FinishRecovery $app }
        Remove-FinalizedTransaction $app
        return
    }
    if (Test-Path "$txn/backup") {
        if (Test-Path $app) { Remove-TransactionDirectory $app $app }
        # Copy keeps the only backup available until restored health is confirmed.
        Copy-Item -LiteralPath "$txn/backup" -Destination $app -Recurse
        if ($RestoreBroker) { & $RestoreBroker $app } else { & $RegisterBroker $app }
        if (Test-Path "$app/installed-version.json") { & $HealthCheck $app }
    } elseif ($state.hadPrevious) {
        if ($state.phase -ne 'prepared') { throw 'Recovery required: previous payload backup is unavailable' }
        if ($RestoreBroker) { & $RestoreBroker $app }
        & $HealthCheck $app
    } else {
        if (-not $RestoreBroker) { throw 'Recovery required: first installation did not commit; component recovery is required' }
        & $RestoreBroker $app
        if (Test-Path $app) { Remove-TransactionDirectory $app $app }
    }
    $state.phase = 'restored'; Write-TransactionState $txn $state
    if ($FinishRecovery) { & $FinishRecovery $app }
    Remove-FinalizedTransaction $app
}
function Invoke-InstallTransaction {
    param([string]$AppDirectory, [string]$PayloadDirectory, [string]$Version,
        [scriptblock]$Prepare, [scriptblock]$RegisterBroker, [scriptblock]$HealthCheck,
        [scriptblock]$BackupBroker, [scriptblock]$RestoreBroker, [switch]$DeferCommit,
        [scriptblock]$FinishRecovery, [scriptblock]$ValidateStaged)
    $app = Assert-TransactionPath $AppDirectory; $txn = "$app.transaction"
    if (Test-Path $txn) { throw "Recovery required: unfinished transaction at $txn. Recover it before installing." }
    Assert-OwnedInstallation $app
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid stable version' }
    if (Test-Path "$app/installed-version.json") {
        $old = Get-Content "$app/installed-version.json" -Raw | ConvertFrom-Json
        if ([version]$old.version -gt [version]$Version) { throw 'Downgrade is not supported' }
    }
    & $Prepare $app
    $state = [ordered]@{ schemaVersion = 1; version = $Version; hadPrevious = (Test-Path $app); phase = 'staging'; brokerBackupAcknowledged = $false }
    New-Item -ItemType Directory -Path $txn -Force | Out-Null
    try {
        Write-TransactionState $txn $state
        New-Item -ItemType Directory -Path "$txn/staged" -Force | Out-Null
        Copy-Item -Path "$PayloadDirectory/*" -Destination "$txn/staged" -Recurse -Force
        if ($ValidateStaged) { & $ValidateStaged "$txn/staged" }
        if (Test-Path $app) {
            # Preserve Inno's existing uninstall log across repair or upgrade.
            Get-ChildItem -LiteralPath $app -Filter 'unins*' -File | Copy-Item -Destination "$txn/staged"
        }
        @{ version = $Version; productId = 'usb-web-launcher' } | ConvertTo-Json | Set-Content "$txn/staged/installed-version.json" -Encoding UTF8
        Write-TransactionState $txn $state
        if ($BackupBroker) {
            $state.phase = 'snapshot-started'; Write-TransactionState $txn $state
            & $BackupBroker $app
            $state.brokerBackupAcknowledged = $true
        }
        $state.phase = 'prepared'; Write-TransactionState $txn $state
        if ($state.hadPrevious) { Move-Item -LiteralPath $app -Destination "$txn/backup" }
        $state.phase = 'backed-up'; Write-TransactionState $txn $state
        Move-Item -LiteralPath "$txn/staged" -Destination $app
        $state.phase = 'files-replaced'; Write-TransactionState $txn $state
        & $RegisterBroker $app
        $state.phase = 'broker-registered'; Write-TransactionState $txn $state
        & $HealthCheck $app
        $state.phase = 'verified'; Write-TransactionState $txn $state
    } catch {
        $cause = $_.Exception.Message
        if (-not (Test-Path "$txn/state.json")) { Remove-TransactionDirectory $txn $app; throw $cause }
        if ($state.phase -eq 'snapshot-started' -and -not $state.brokerBackupAcknowledged) {
            # Backup is read-only and cleans any caught partial snapshot itself.
            # A hard interruption retains snapshot-started for explicit recovery.
            Remove-TransactionDirectory $txn $app
            throw "Broker snapshot failed before application replacement: $cause"
        }
        try { Restore-InstallTransaction $app $RegisterBroker $HealthCheck $RestoreBroker $FinishRecovery }
        catch { throw "Recovery required: $cause. Recovery failed: $($_.Exception.Message). Backup retained at $txn" }
        throw "Installation failed. Previous version restored. $cause"
    }
    if (-not $DeferCommit) { Complete-InstallTransaction $app { param($path) } }
}
function Complete-InstallTransaction([string]$AppDirectory, [scriptblock]$CommitBroker) {
    $app = Assert-TransactionPath $AppDirectory; $txn = "$app.transaction"
    $state = Get-Content "$txn/state.json" -Raw | ConvertFrom-Json
    if ($state.phase -notin @('verified','committing','committed')) { throw 'Cannot commit an unverified installation' }
    if ($state.phase -ne 'committed') {
        if (-not $CommitBroker) { throw 'Recovery required: broker commit callback is missing' }
        $state.phase = 'committing'; Write-TransactionState $txn $state
        & $CommitBroker $app
        $state.phase = 'committed'; Write-TransactionState $txn $state
    }
    Remove-FinalizedTransaction $app
}
Export-ModuleMember -Function Invoke-InstallTransaction,Restore-InstallTransaction,Complete-InstallTransaction
