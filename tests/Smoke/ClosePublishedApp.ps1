param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$startedAt = Get-Date
$process = Start-Process -FilePath $resolvedExecutable `
    -ArgumentList '--smoke-test-no-devices' `
    -WindowStyle Hidden `
    -PassThru

try {
    $readyTitle = 'USB Web Launcher [Smoke Ready]'
    $windowDeadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not $process.HasExited -and $process.MainWindowTitle -ne $readyTitle) {
        if ([DateTime]::UtcNow -ge $windowDeadline) {
            throw 'The app did not report smoke readiness within 15 seconds.'
        }

        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }

    if ($process.HasExited) {
        throw "The app exited before reporting smoke readiness. Exit code: $($process.ExitCode)."
    }

    if (-not $process.CloseMainWindow()) {
        throw 'Could not send a normal close request to the app main window.'
    }

    if (-not $process.WaitForExit(15000)) {
        throw 'The app did not exit within 15 seconds after the close request.'
    }

    if ($process.ExitCode -ne 0) {
        throw "The app returned a non-zero exit code after close: $($process.ExitCode)."
    }

    $eventDeadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $runtimeCrashes = Get-WinEvent -FilterHashtable @{
            LogName = 'Application'
            Id = 1026
            StartTime = $startedAt
        } -ErrorAction SilentlyContinue | Where-Object {
            $_.ProcessId -eq $process.Id -and
            $_.Message -match 'UsbWebLauncher\.App\.exe'
        }

        if ($runtimeCrashes) {
            throw 'The app generated a .NET Runtime crash event while closing.'
        }

        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $eventDeadline)
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}
