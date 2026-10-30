## Why

Static review of the current executable path found several cleanup chains where one exception can prevent later resources from being released, plus a browser-session directory leak when WebView2 initialization fails or is cancelled. The same review also found avoidable release payload and legacy diagnostic code, and a PIN setup dialog that does not yet follow the repository's short-lived secret-buffer design.

## What Changes

- Make application, business-session, WebView2, USB-monitor, update-service, event-subscription, and background-task ownership explicit and exception resilient.
- Ensure failed, cancelled, stopped, retried, and closing flows retire browser session directories, observe background faults, detach handlers, and release every owned resource.
- Isolate asynchronous event subscribers so one failing subscriber cannot suppress later subscribers or abort disposal.
- Change local PIN setup to use a masked `PasswordBox` and the existing secure PIN reader, without materializing the PIN as an ordinary managed string, and clear transient buffers on every exit path.
- Remove the unreferenced popup/statement diagnostic chain while retaining the actively used diagnostic bundle exporter.
- Reduce the release payload to the supported `zh-Hans` satellite resources, measure App single-file publishing against compatibility gates, and retain the current self-contained, non-trimmed deployment contract.
- Add focused regression tests and release-payload baselines for cleanup failures, secret-buffer lifetime, event unsubscription, file count, and unpacked size.

## Capabilities

### New Capabilities

- `runtime-resource-lifecycle`: Defines deterministic, exception-resilient cleanup and fault observation for runtime sessions, browser data, device monitors, event subscribers, update resources, and background work.
- `release-payload-footprint`: Defines the supported-language and compatibility gates for reducing the shipped self-contained payload without deleting required runtime assets or enabling trimming.

### Modified Capabilities

- `manual-launcher-actions`: Requires local PIN entry to be masked and transferred through short-lived clearable buffers without creating an ordinary managed PIN string.

## Impact

- Runtime composition and shutdown in `UsbWebLauncher.App`, especially `App.xaml.cs`, session composition, WebView2 ownership, update presentation, and PIN setup.
- Windows device-monitor and credential-related resource ownership in `UsbWebLauncher.Windows`.
- Release configuration and validation in `Directory.Build.props`, project files, `build/build.ps1`, and `build/Packaging.psm1`.
- Removal of unused popup/statement diagnostic classes, scripts, embedded resources, and their tests; the diagnostic bundle exporter remains supported.
- New and extended unit/integration tests under `tests/UsbWebLauncher.Tests`.
- No intended change to installer identity, update protocol, bank workflow, certificate authorization, self-contained deployment, or supported Windows architecture.
