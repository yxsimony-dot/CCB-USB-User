## 1. Baseline and diagnosis

- [x] 1.1 Run the existing no-secret hardware and broker prerequisites against the current Tendyron Key.
- [x] 1.2 Verify the published application, broker executable, scheduled task, active profile, and application-image binding.
- [x] 1.3 Reproduce the fixed-PIN setup flow through the local UI without recording the PIN.
- [x] 1.4 Capture only public process/window/control metadata for the native vendor password dialog and compare it with the active profile.

## 2. Implementation correction

- [x] 2.1 Correct the current Tendyron certificate, CSP, container, key-spec, TLS host, and broker policy hash.
- [x] 2.2 Preserve fail-closed behavior for every identity mismatch, focus change, timeout, duplicate candidate, cancellation, and broker failure.
- [x] 2.3 Preserve one-attempt-per-USB-cycle behavior and clear all short-lived PIN buffers on every exit path.
- [x] 2.4 Package and validate the official CCB UKey extension, including its manifest-derived ID and required files.
- [x] 2.5 Enable WebView2 browser extensions and install or enable the verified extension before navigation.
- [x] 2.6 Permit distinct navigation and certificate TLS hosts, including the two observed exact authentication endpoints, while retaining exact host, port, and certificate request matching.
- [x] 2.7 Add or update deterministic tests using random test secrets only.
- [x] 2.8 Separate PIN input from submit activation, wait for the exact submit control to become enabled, and revalidate the complete prompt identity before one bounded click.
- [x] 2.9 Add bounded target-only foreground activation for an exactly matched background dialog, then require the exact dialog and PIN input control to be foreground/focused before sending any input.
- [x] 2.10 Add deterministic tests for successful background-dialog activation, activation refusal, focus loss, identity change, cancellation, and preservation of the remaining authentication timeout.
- [x] 2.11 Require an initially empty exact PIN input control and wait until it has processed exactly the in-memory PIN character count before activating submit, without reading or logging the PIN text or counts.
- [x] 2.12 Add deterministic tests for partial input processing, unexpected input state, count-read failure, timeout, cancellation, and single-submit ordering.

## 3. Real-device acceptance

- [x] 3.1 Validate the path where the Tendyron Key is inserted before the launcher starts.
- [ ] 3.2 Validate the path where the Tendyron Key is inserted after the launcher is already running.
- [x] 3.3 Confirm automatic target certificate selection, token unlock, native prompt autofill, native prompt confirmation, and target TLS success.
- [ ] 3.4 Confirm a failed or mismatched prompt performs no input and does not create a formal validation stamp.
- [ ] 3.5 Confirm removing and reinserting the Key creates a new cycle without replaying the prior attempt.

## 4. Release and handoff

- [x] 4.1 Run the full build and automated test suite.
- [x] 4.2 Build and install the final self-contained `win-x64` package and current-image broker.
- [x] 4.3 Re-scan source, configuration, tests, logs, IPC traces, and release output for real PIN or unsafe secret transport.
- [x] 4.4 Record the rollback directory, broker uninstall command/path, and Windows Credential Manager cleanup method without recording the PIN.
