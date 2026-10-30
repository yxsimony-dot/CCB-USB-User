param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,
    [string] $ReportPath,
    [ValidateRange(1, 10)]
    [int] $Runs = 3
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $ReportPath) {
    $ReportPath = Join-Path $repo 'artifacts/test-results/lifecycle-observation.json'
}
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
$reportDirectory = Split-Path $ReportPath -Parent
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null

$workRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'UsbWebLauncher-Lifecycle-' + [guid]::NewGuid().ToString('N'))
$runReportPath = Join-Path $workRoot 'current-run.json'
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
$runReports = @()
$failure = $null

try {
    for ($run = 1; $run -le $Runs; $run++) {
        Remove-Item -LiteralPath $runReportPath -Force -ErrorAction SilentlyContinue
        $quotedReportPath = '"' + $runReportPath.Replace('"', '\"') + '"'
        $process = Start-Process -FilePath $resolvedExecutable `
            -ArgumentList "--lifecycle-observation-report=$quotedReportPath" `
            -WindowStyle Hidden `
            -PassThru

        try {
            $readyTitle = 'USB Web Launcher [Lifecycle Observation Ready]'
            $windowDeadline = [DateTime]::UtcNow.AddSeconds(180)
            while (-not $process.HasExited -and $process.MainWindowTitle -ne $readyTitle) {
                if ([DateTime]::UtcNow -ge $windowDeadline) {
                    throw "Lifecycle run $run did not report readiness within 180 seconds."
                }

                Start-Sleep -Milliseconds 100
                $process.Refresh()
            }

            if ($process.HasExited) {
                throw "Lifecycle run $run exited before readiness. Exit code: $($process.ExitCode)."
            }
            if (-not (Test-Path -LiteralPath $runReportPath -PathType Leaf)) {
                throw "Lifecycle run $run did not write its JSON report."
            }

            $runReport = Get-Content -LiteralPath $runReportPath -Raw | ConvertFrom-Json
            $runReports += $runReport
            if (-not $runReport.success) {
                $failureText = @($runReport.failures) -join '; '
                throw "Lifecycle run $run failed validation: $failureText"
            }
            if (@($runReport.cycles).Count -ne $runReport.tolerances.cycleCount) {
                throw "Lifecycle run $run reported an incomplete cycle set."
            }
            $finalStop = @($runReport.cycles)[-1].stopped
            if ($finalStop.liveBusinessSessionOwners -ne 0 -or
                $finalStop.activeBrowserSessionDirectories -ne 0 -or
                $finalStop.retiredBrowserSessionDirectories -ne 0) {
                throw "Lifecycle run $run left observable owners or browser session directories."
            }

            if (-not $process.CloseMainWindow()) {
                throw "Could not send a normal close request during lifecycle run $run."
            }
            if (-not $process.WaitForExit(15000)) {
                throw "Lifecycle run $run did not exit within 15 seconds after close."
            }
            if ($process.ExitCode -ne 0) {
                throw "Lifecycle run $run returned exit code $($process.ExitCode)."
            }
        }
        finally {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }

    $aggregate = [ordered]@{
        schemaVersion = 1
        completedAtUtc = [DateTimeOffset]::UtcNow
        executablePath = $resolvedExecutable
        runCount = $Runs
        success = $true
        runs = $runReports
    }
    $aggregate | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $ReportPath -Encoding utf8
    Write-Host "Lifecycle observation passed. Report: $ReportPath"
}
catch {
    $failure = $_
    $aggregate = [ordered]@{
        schemaVersion = 1
        completedAtUtc = [DateTimeOffset]::UtcNow
        executablePath = $resolvedExecutable
        runCount = $runReports.Count
        success = $false
        failure = $_.Exception.Message
        runs = $runReports
    }
    $aggregate | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $ReportPath -Encoding utf8
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failure) {
    throw $failure
}
