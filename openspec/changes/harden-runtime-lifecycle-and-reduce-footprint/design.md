## Context

See `proposal.md` for motivation and the delta specs for required behavior. The current implementation already contains several useful lifecycle primitives, but ownership is incomplete at a few boundaries:

- `WpfWebView2Control` creates a session directory before `CoreWebView2Environment.CreateAsync`; if that call fails, `_environment` remains null and disposal has no path that retires the created directory.
- `App.DisposeRuntimeCoreAsync` starts cleanup owners together, but `Task.WhenAll` can throw before `Session.Dispose` and `_session` clearing execute.
- `WindowsUsbDeviceMonitor.DisposeAsync` can skip later resources when `Stop` or a tracked task faults. Its fire-and-forget observer handles cancellation but not ordinary faults, and event delivery stops at the first failing subscriber.
- `SessionUsbDeviceMonitor` has the same subscriber-coupling behavior.
- the update service owns synchronization primitives and a production `HttpClient`, and retains selected releases and downloaded paths without a terminal ownership or bounded-retention policy.
- `LocalPinCommands` subscribes with an anonymous delegate that cannot be detached.
- `PinSetupDialog` currently uses `TextBox.Text`, although `SecurePasswordPinReader` already provides the intended `SecureString` to clearable-buffer conversion.
- the application project still embeds a popup/statement diagnostic sampling chain that has no production construction site. `DiagnosticBundleExporter` is separate and remains invoked from `MainWindow`.
- release publishing is self-contained but does not limit satellite languages. The broker is already single-file; the WPF/WebView2 App is not.

The current release contract and unfinished installer/update changes remain authoritative for identity, versioning, installation, and update behavior. This change may tighten payload validation but does not redesign those protocols.

## Goals / Non-Goals

**Goals:**

- Give every created disposable, subscription, task, native source, temporary directory, and service a named owner and terminal release path.
- Make cleanup best effort across independent owners while preserving a complete error result for diagnostics and tests.
- Prevent secret material from becoming a normal managed string in the local PIN dialog.
- Remove confirmed dead production diagnostics and unsupported localization resources.
- Establish measured payload regression gates and evaluate App single-file publishing without weakening runtime compatibility.

**Non-Goals:**

- Rewriting session orchestration, USB detection rules, certificate selection, native prompt authorization, bank automation, installer transactions, or the update protocol.
- Enabling .NET trimming, deleting runtime files by pattern, or changing from self-contained `win-x64`.
- Merging the presence monitor and session monitor in this change. They have different lifetime and identity semantics; consolidation can be proposed separately after lifecycle ownership is stable.
- Removing the diagnostic bundle exporter or active bounded certificate/session lifecycle logging.
- Treating static review as proof of a native heap leak; the implementation will fix demonstrated ownership hazards and add repeatable memory/handle checks.

## Decisions

### 1. Cleanup uses an exception-collecting stage runner

Application and component cleanup will use a small shared pattern that invokes every named release stage, captures failures, and throws or reports one aggregate only after all stages and final ownership clearing have run. Final session disposal and `_session` clearing belong in `finally`, not after `Task.WhenAll`.

Independent owners may still begin cancellation concurrently to preserve the existing exit budget. Their completion results will be awaited through wrappers that never short-circuit sibling cleanup. Repeated stop and close requests continue to share one cleanup task; a failed cleanup task remains observable, but it cannot leave the application pointing at the released session.

Alternative considered: swallow every cleanup exception. Rejected because it hides real release failures and makes regression testing impossible.

### 2. Browser directory ownership begins when the directory is created

`WpfWebView2Control` will track directory state separately from `_environment`: not-created, active/pending-environment, waiting-for-browser-exit, and retired. If environment creation fails before an environment exists, the directory can be retired immediately. If an environment exists, disposal detaches controller events only after arranging browser-exit observation; retirement then uses the existing rename/delete mechanism.

Deletion failure remains non-fatal and leaves a retired marker for `CleanupRetired` on a later startup. This avoids force-deleting a live WebView2 profile and avoids inventing a process-kill path.

Alternative considered: delete the directory unconditionally in `catch`. Rejected because cancellation can race after the environment and browser process already exist.

### 3. Monitor shutdown and publication do not await user code while holding ownership locks

Both USB monitor layers will snapshot the current invocation list and session generation under their lock, then invoke handlers outside the lock. Each handler is awaited in its own `try/catch`; later handlers still run, and failures are collected for diagnostics rather than escaping a native callback boundary.

`WindowsUsbDeviceMonitor.DisposeAsync` will detach first, request cancellation, attempt `Stop`, await all tracked tasks through fault-observing wrappers, then dispose debounce/lifetime sources, gates, and the change source in `finally`-style stages. `ObserveAsync` will handle ordinary exceptions as well as cancellation and always remove the task from `_refreshes`.

Alternative considered: use `Task.WhenAll` for subscriber delivery. Rejected because concurrent handlers would change ordering and can complicate current-session revocation semantics.

### 4. Fire-and-forget work must have a named observer

Every intentionally unawaited task will be routed through a helper or local observer that records non-cancellation failure and removes any tracking entry. This includes startup update checks, presence-observer startup, directory cleanup, and monitor refresh tasks. Native `async void` handlers remain only where the framework requires them and must contain a complete exception boundary.

The rule is narrow: logging a failure does not convert a failed business operation into success, and diagnostics must follow the existing no-secret policy.

### 5. Update resources receive explicit ownership and bounded registries

`IApplicationUpdateService` will gain an explicit disposal contract. The production factory owns and disposes its `HttpClient`; injected clients use an explicit ownership setting so tests and alternate hosts are not disposed accidentally. The service also disposes its gates and a lifetime cancellation source.

The presentation controller owns the service instance supplied by App composition, cancels pending operations, detaches `PropertyChanged`, and disposes the service after operations can no longer publish UI state. Selected releases and downloaded paths will be reduced to the current actionable candidate/download or pruned whenever a new check replaces state. This preserves anti-confusion validation without retaining the lifetime history.

Alternative considered: replace the service with dependency-injection infrastructure or `IHttpClientFactory`. Rejected as unnecessary architecture for this desktop process.

### 6. Subscription ownership is represented by disposable components

`LocalPinCommands` will store a named prerequisite handler and implement the disposal contract used by `ManualLauncherActions`/session cleanup. Disposal detaches that handler and clears its outward event. Tests will count subscriptions before and after session replacement.

Other touched components will follow the same rule: the object that subscribes is responsible for detaching, and its containing runtime owns that object's disposal.

### 7. PIN input uses the existing secure conversion boundary

The XAML control changes from `TextBox` to `PasswordBox`, and dialog completion reads `SecurePassword` through `SecurePasswordPinReader.CopyFrom`. `PinSetupDialog` clears the `PasswordBox` in a `finally` path for success, cancellation, and exceptions. The returned `ClientCertificatePin` remains owned by `LocalPinCommands`, which already disposes it after `SaveFixedAsync`.

No validation or status message may call `PasswordBox.Password`, interpolate the value, or create a normal managed string. Tests use random non-production secrets and inspect disposal/clearing behavior rather than secret content.

Alternative considered: bind a password string into the view model. Rejected because WPF binding would extend the lifetime of a managed plaintext string.

### 8. Dead diagnostics are removed by reachability, not by broad naming rules

The popup/statement sampler classes, their two JavaScript resources, project embeddings, and tests that exist only for that chain will be deleted after a repository-wide reference check. `DiagnosticBundleExporter`, its tests, active known-popup handling, export scripts, and certificate/session diagnostics remain.

This is a source deletion, not a runtime feature flag. A future investigation that needs continuous DOM/resource sampling must introduce a separate, explicitly temporary diagnostic change rather than leaving dormant production code.

### 9. Payload reduction starts with language filtering and measured gates

`SatelliteResourceLanguages` will be set to `zh-Hans` at the common publish boundary used by App and broker. Payload validation will reject unsupported culture directories and write summary metrics alongside the existing per-file manifest.

The App will then be published once as a single-file candidate. Adoption requires the existing .NET, JavaScript, packaging, payload-integrity, installer, and smoke suites, plus checks for external `launcher-profile.json`, browser extension files, broker policy, signing targets, and update handoff. Failure keeps the current conventional self-contained layout and records the reason. `PublishTrimmed` remains false in both paths.

The first accepted reduced build establishes reviewed maximum file-count and unpacked-byte bounds. Future increases must update those bounds explicitly with a rationale; installer compression alone is not used as the regression metric.

## Risks / Trade-offs

- [Cleanup now surfaces multiple latent errors] -> Aggregate stage names after all owners are released, and keep user-facing shutdown bounded by the existing watchdog.
- [Browser-process exit may not arrive before process shutdown] -> Retire only after safe ownership conditions and rely on the next-start retired-directory cleanup for locked remnants.
- [Subscriber isolation can hide a broken consumer] -> Record each failure with component/event identity and add deterministic tests that prove later delivery.
- [Disposing an injected HTTP client can break tests or another owner] -> Make client ownership explicit at construction and test both owned and borrowed cases.
- [Single-file WPF/WebView2 behavior differs from ordinary publish] -> Treat it as a gated candidate, retain external assets, and keep the conventional layout on any failure.
- [A strict size bound can block legitimate dependency updates] -> Store bounds in source with measured deltas and require an explicit reviewed update rather than an undocumented bypass.
- [Deleting historical diagnostics reduces ad hoc investigation options] -> Retain the supported bundle exporter and reintroduce targeted temporary instrumentation only through a scoped change.

## Migration Plan

1. Add lifecycle and secret-buffer regression tests that fail against the current implementation.
2. Fix component-level cleanup and event isolation, then update App composition and shutdown ownership.
3. Remove unreachable diagnostics and verify the remaining exporter and bank workflows.
4. Apply satellite-language filtering, generate a clean payload, and establish file-count/byte baselines.
5. Evaluate the App single-file candidate through all compatibility gates and either adopt it or record the measured rejection while retaining conventional publish.
6. Run the complete non-hardware test suite, packaging tests, payload validation, and startup smoke test; perform a bounded repeated start/stop/close memory and handle observation.
7. Roll back by restoring the previous publish layout and payload bounds if packaging or real-machine startup fails. Lifecycle and PIN hardening remain independently deployable.
