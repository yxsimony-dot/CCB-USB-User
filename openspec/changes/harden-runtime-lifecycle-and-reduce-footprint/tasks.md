## 1. Cleanup Foundation And Application Ownership

- [x] 1.1 Add an exception-collecting named cleanup-stage runner that attempts every registered stage, preserves stage identity, and reports an aggregate without credential-bearing data; verify focused tests prove later stages run after synchronous and asynchronous failures and that diagnostics contain no PIN, page body, or credential URL.
- [x] 1.2 Refactor `App.DisposeRuntimeCoreAsync` to snapshot and revoke ownership once, release every captured owner through the stage runner, and dispose and clear the captured `BusinessSessionScope` in a `finally` path; verify application lifecycle tests cover a failing owner, later-owner release, `_session` clearing, and repeated callers sharing one cleanup task.
- [x] 1.3 Route intentionally unawaited startup update checks, presence-observer startup, browser-directory cleanup, and session revocation work through named fault observers while retaining complete exception boundaries on required WPF `async void` handlers; verify tests or diagnostic captures prove ordinary faults are observed once and expected cancellation is ignored.
- [x] 1.4 Include the presence observer and update presentation ownership in the shutdown cleanup stages without extending the existing exit watchdog; verify `ApplicationShutdownSequenceTests` cover cleanup failure and timeout while still closing the window and attempting all captured release stages.

## 2. Browser Session Directory Lifecycle

- [x] 2.1 Track browser session-directory ownership independently from WebView2 environment assignment, including pending-environment, waiting-for-browser-exit, and retired states; verify `WpfWebView2ControlDisposalTests` cover each state transition and idempotent disposal.
- [x] 2.2 Retire and schedule cleanup for a directory when environment creation fails before a live environment is returned; verify an injected environment-creation failure leaves no active directory owner and `BrowserSessionDirectoryTests` prove immediate deletion or a retryable retired marker.
- [x] 2.3 Handle cancellation or disposal after environment creation by releasing the controller, observing the owned browser-process exit, and retiring the directory only when safe; verify tests prove a live profile is not deleted early and a locked retired directory is eligible for bounded cleanup on a later startup.

## 3. USB Monitor Fault And Subscriber Isolation

- [x] 3.1 Refactor `WindowsUsbDeviceMonitor.DisposeAsync` into exception-resilient detach, cancel, stop, tracked-task settlement, and resource-disposal stages; verify `WindowsUsbDeviceMonitorTests` prove a throwing `Stop` and a faulted refresh task do not skip cancellation-source, semaphore, or change-source disposal.
- [x] 3.2 Extend monitor task observation to record non-cancellation faults exactly once and always remove completed tasks from `_refreshes`; verify tests force enumeration and publication faults and confirm disposal reaches a terminal state without an unobserved task exception.
- [x] 3.3 Publish Windows USB notifications from a stable subscriber snapshot outside ownership locks and isolate each asynchronous handler failure; verify a failing first subscriber is reported while later subscribers receive the same snapshot in registration order.
- [x] 3.4 Apply the same stable-snapshot and per-subscriber isolation behavior to `SessionUsbDeviceMonitor` while preserving synchronous session revocation; verify tests cover a failing subscriber, later delivery, removal races, no callbacks after disposal begins, and unchanged original-Key identity semantics.

## 4. Service And Subscription Ownership

- [x] 4.1 Give `LocalPinCommands` a disposal contract with a named prerequisite-state handler, detach it during disposal, and clear outward subscribers; verify tests count prerequisite subscriptions before and after session replacement and prove later source events cannot retain or invoke the released command object.
- [x] 4.2 Make `ManualLauncherActions` release its owned local PIN command component exactly once alongside its existing subscriptions and page-login owner; verify `ManualLauncherActionsTests` cover repeated disposal and a child-disposal failure without leaving other handlers attached.
- [x] 4.3 Add an explicit asynchronous disposal contract to `IApplicationUpdateService` and `GitHubApplicationUpdateService`, including lifetime cancellation, gate disposal, and explicit owned-versus-borrowed `HttpClient` behavior; verify update-service tests cover both client ownership modes and disposal during a pending check or download.
- [x] 4.4 Replace lifetime-growing update release and downloaded-path registries with current actionable state or a documented fixed bound; verify repeated check/download tests show retained entry counts remain bounded and stale candidates cannot be downloaded or verified.
- [x] 4.5 Make `UpdatePresentationController` cancel and settle pending work, detach `LauncherViewModel.PropertyChanged`, and dispose its service before shutdown completes; verify controller tests prove no late UI publication after disposal and App shutdown awaits the controller through the cleanup-stage runner.

## 5. PIN Secret Handling

- [x] 5.1 Replace the PIN `TextBox` with a masked `PasswordBox` and use `SecurePasswordPinReader.CopyFrom` without reading `PasswordBox.Password` or creating a managed plaintext PIN string; verify focused WPF/reader tests cover masked input, empty input, successful conversion, and clearing of unmanaged and temporary character buffers.
- [x] 5.2 Clear the password control and dispose every transient `ClientCertificatePin` on success, cancellation, validation failure, save failure, and unexpected dialog closure while transferring successful ownership exactly once to the save operation; verify `LocalPinCommandsTests` cover every exit path with non-production random secrets and assert no secret appears in status or diagnostics.

## 6. Dead Diagnostic Removal

- [x] 6.1 Perform a repository-wide reachability check, then remove the unused popup/statement sampler classes, `CcbPopupDiagnostics.js`, `CcbStatementDiagnostics.js`, their embedded-resource entries, and tests that exist only for that chain; verify a reference search finds no remaining production references to the removed symbols or assets.
- [x] 6.2 Preserve `DiagnosticBundleExporter`, known-popup handling, statement/receipt export behavior, and active bounded lifecycle diagnostics; verify the remaining .NET browser tests and `npm test` pass after deletion and the diagnostic bundle export command still produces its expected archive.

## 7. Release Footprint And End-To-End Verification

- [x] 7.1 Set the common release publish boundary to `SatelliteResourceLanguages=zh-Hans` while keeping `win-x64`, self-contained deployment, and `PublishTrimmed=false`; verify publish-property and packaging tests reject unsupported culture directories and confirm required `zh-Hans`, WebView2, extension, profile, broker, installer, and update assets remain.
- [x] 7.2 Extend payload manifest generation with machine-readable file-count and unpacked-byte totals plus committed maximum bounds; verify packaging tests pass within the bounds and fail with measured deltas when synthetic file count or size exceeds them.
- [x] 7.3 Produce a clean reduced conventional payload, record its reviewed file-count and unpacked-size baseline, and update the committed bounds with an explanation of the reduction; verify `Assert-CleanPayload`, payload-integrity tests, and installer packaging accept the baseline without filename-pattern runtime deletion.
- [x] 7.4 Publish the main App once as a single-file candidate and run the full runtime, JavaScript, packaging, payload-integrity, installer, signing-target, update-handoff, and startup-smoke gates; verify the production configuration adopts single-file only if every gate passes, otherwise retains the conventional layout and records the measured rejection reason in source-controlled build metadata.
- [x] 7.5 Add a repeatable bounded lifecycle observation that exercises multiple start, stop, retry, and close cycles while recording process private bytes, managed heap, handle count, browser-process exit, and retired-session-directory count; verify the harness exits successfully within documented tolerances and leaves no accumulating live session owners.
- [x] 7.6 Run `dotnet test UsbWebLauncher.sln -c Release --filter "Category!=Hardware"`, `npm test`, all packaging test scripts, a clean self-contained publish, and the startup smoke test; verify all gates pass and document any hardware-only checks that still require real-machine acceptance.
