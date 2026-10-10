# Design

## Current implementation

The application already has these boundaries:

- `PinSetupDialog` collects the PIN through a WPF `PasswordBox`.
- `LocalPinCommands` validates and saves the fixed PIN through the Windows Credential Manager adapter.
- The authentication coordinator reads the credential once per USB insertion cycle.
- Tendyron is configured through the current certificate, CSP, container, and `AT_SIGNATURE`/`PP_SIGNATURE_PIN` path.
- `WindowsNativePromptContinuationClient` arms the broker only after the token has accepted the PIN, the exact certificate has been selected, and target TLS is pending.
- `NativePromptBroker` validates the peer, process, publisher, window identity, owner/root relationship, control IDs, focus, and attempt ID before sending one virtual-key input batch.

Real-device diagnosis found that the CryptoAPI unlocker performs a startup
`CryptSignHash` probe immediately after setting the stored PIN. For the current
Tendyron provider, that probe opens the vendor password dialog in the launcher
process before WebView2 exists and before the broker can be bound to the target
TLS attempt. The remaining work is therefore completion and correction of this
ordering in the real-device path, not a new unrestricted keyboard automation
feature.

Further comparison with the working Edge flow found a second prerequisite:
ordinary Edge has the official `中国建设银行Ukey扩展` extension installed, but
WebView2 browser extensions are disabled by default. The launcher must create
its WebView2 environment with extension support, validate the packaged official
extension as ID `ahjacjkbgeefngpfnfhccehkfnaeahbf` version `1.0.0.9`, and install
or enable it before navigation.

The CCB entry page is hosted on `b2bbank.ccb.com`, while observed
client-certificate TLS requests use `b2b3.ccb.com:443` and
`xc.b2b3.ccb.com:443`. These are separate roles. Profile validation permits
distinct HTTPS navigation and authentication hosts, while
`ClientCertificateRequestMatcher` authorizes only the explicitly configured
authentication host/port pairs and exact certificate.

Live-window diagnosis of the published application confirmed that the TLS-stage
dialog is a top-level `#32770` window owned by `msedgewebview2.exe`. Its visible
PIN control is `Edit` ID `3743`, and its visible submit control is `Button` ID
`4000`. The release profile already contains those values; the earlier
`1010`/`1` design notes were stale.

The same diagnosis found that the dialog can be visible while its GUI thread has
neither an active window nor a focused control. `WindowsNativePromptExecutor`
currently calls `SetForegroundWindow`, ignores its return value, and then spends
the remaining shared 30-second attempt budget waiting for the dialog to become
the foreground window. If another application retains the foreground, the
executor never reaches input focus or `SendInput`, and the continuation is
cancelled when the client-certificate attempt times out.

## Technical approach

1. Run the existing no-secret diagnostics and capture only public window/process metadata for the current prompt.
2. Reproduce the flow from the published directory with the current Tendyron Key.
3. Compare the observed native dialog with the release profile:
   - `msedgewebview2.exe`;
   - `#32770`;
   - trusted root window `USB Web Launcher`;
   - input class `Edit`, control ID `3743`;
   - submit class `Button`, control ID `4000`.
4. Package and validate the official CCB UKey extension:
   - manifest-derived ID `ahjacjkbgeefngpfnfhccehkfnaeahbf`;
   - name `中国建设银行Ukey扩展`;
   - version `1.0.0.9`;
   - native-messaging permission and CCB content-script match;
   - all referenced scripts and icons.
5. Create WebView2 with `AreBrowserExtensionsEnabled = true`, then install or
   enable the verified extension before registering navigation.
6. Keep `b2bbank.ccb.com` as the navigation entry point and configure
   `b2b3.ccb.com:443` plus `xc.b2b3.ccb.com:443` as the exact
   client-certificate endpoint allowlist.
7. When native prompt continuation is enabled, keep the silent provider, PIN,
   and key acquisition steps but defer the synthetic signature probe. Retain the
   provider and key handles so the real target TLS signature operation uses the
   unlocked session after the exact certificate is selected and the broker is
   armed.
8. Preserve the existing synthetic signature probe for configurations that do
   not enable native prompt continuation.
9. If a TLS-stage dialog mismatch is found, update the smallest responsible layer:
   - release profile for stable public identity data;
   - window matcher for the observed owner/control relationship;
   - broker coordination for timing/focus;
   - input builder for the current keyboard layout;
   - authentication coordination for the pending-marker and TLS completion state.
10. After selecting exactly one fully matched dialog, attempt bounded foreground
    activation for that exact top-level window. Attach input queues only for the
    minimum activation/focus operation, detach them on every exit path, and do
    not broaden the authorized process or window set.
11. Re-capture and revalidate the complete dialog identity after activation.
    Require the exact dialog to be the system foreground window and the exact
    `Edit` ID `3743` control to be the dialog thread's focused control before
    sending input. If foreground activation is refused, the target changes, or
    focus cannot be established, send no PIN and fail closed without consuming
    the full client-certificate timeout.
12. Send the PIN as one PIN-only virtual-key batch. Wait within the remaining
    bounded attempt timeout for the exact `Edit` ID `3743` input control to
    report exactly the expected character count through a bounded
    `WM_GETTEXTLENGTH` message. Do not retrieve the input text and do not record
    either the expected or observed count. Require the control to be empty
    before input, fail closed on an unavailable, excess, or timed-out count,
    then wait for the exact `Button` ID `4000` submit control to become visible
    and enabled. Recapture and revalidate the complete window identity before
    activating that exact button once with a bounded native message.
13. Add or update deterministic tests for every corrected branch using random test secrets only.
14. Rebuild and reinstall the self-contained package, then run both real-device insertion paths.
15. Confirm that a successful TLS handshake upgrades the pending validation marker to the normal validation stamp, while any mismatch or failure sends no input and does not save a false success.

## Data flow

```text
Local PasswordBox
    -> Credential Manager (current Windows user)
    -> USB insertion cycle
    -> verified official CCB UKey extension enabled in WebView2
    -> navigate to b2bbank.ccb.com
    -> Tendyron provider PIN + key acquisition
    -> exact WebView2 client certificate selection for b2b3.ccb.com:443
       or xc.b2b3.ccb.com:443
    -> broker arm request over the current-user named pipe
    -> target TLS private-key operation
    -> strict native dialog match
    -> bounded target-only foreground activation and input focus
    -> one PIN input batch + one submit
    -> target TLS success
    -> validation stamp
```

The PIN remains in short-lived buffers and is cleared on all completion, cancellation, timeout, and exception paths. It does not pass through configuration, logs, command-line arguments, environment variables, clipboard, or page JavaScript.

## Key files

- `src/UsbWebLauncher.App/Presentation/LocalPinCommands.cs`
- `src/UsbWebLauncher.App/Presentation/WindowsNativePromptBrokerPrerequisites.cs`
- `src/UsbWebLauncher.App/Composition/WindowsClientCertificateAuthenticationRuntime.cs`
- `src/UsbWebLauncher.App/Browser/CcbUKeyBrowserExtensionPackage.cs`
- `src/UsbWebLauncher.App/Browser/CcbUKeyBrowserExtensionInstaller.cs`
- `src/UsbWebLauncher.App/Browser/CcbUKeyWebView2Environment.cs`
- `src/UsbWebLauncher.App/Browser/WpfWebView2Control.cs`
- `src/UsbWebLauncher.App/browser-extensions/ccb-ukey/`
- `src/UsbWebLauncher.Windows/Authentication/WindowsCredentialPinStore.cs`
- `src/UsbWebLauncher.Windows/Authentication/NativePrompt/WindowsNativePromptContinuationClient.cs`
- `src/UsbWebLauncher.Windows/Authentication/NativePrompt/WindowsNativePromptExecutor.cs`
- `src/UsbWebLauncher.Windows/Authentication/NativePrompt/NativePromptWindowMatcher.cs`
- the current Tendyron `launcher-profile.json`

## Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| The vendor dialog is not the configured dialog | Fail closed on mismatch; use no-secret diagnostics to update the exact profile only after observation. |
| The dialog appears after the broker is armed | Keep the bounded polling path and accept only one exact candidate. |
| Virtual-key input depends on the active keyboard layout | Use the existing layout-aware input builder and test non-ASCII rejection without retrying. |
| Another application owns the foreground when the dialog appears | Attempt bounded activation only after full target validation; revalidate foreground, focus, and identity before input; fail closed immediately if activation cannot be established. |
| The submit button becomes enabled before all queued key events are processed | Require an initially empty exact input control and poll only its character count until it equals the in-memory PIN length; never retrieve or log the control text or either count; do not click on timeout, mismatch, or read failure. |
| A failed TLS flow is mistaken for successful setup | Write the formal validation stamp only after target TLS succeeds. |
| A real PIN appears in diagnostics or artifacts | Use only public metadata and random test secrets; perform real PIN entry only through the local UI. |
| A missing or modified extension is loaded | Validate every required file and the manifest-derived official extension ID before WebView2 navigation; fail startup on any mismatch. |
| A page requests the certificate on an unrelated host | Match the WebView2 certificate request against the explicit `b2b3.ccb.com:443` and `xc.b2b3.ccb.com:443` endpoint allowlist and deny every other request. |

## Acceptance

- The broker is installed and bound to the current application image.
- WebView2 enables and loads the verified official CCB UKey extension before opening the CCB entry page.
- Missing, malformed, incomplete, wrong-version, or wrong-ID extension packages stop the flow before navigation.
- Both insertion timings complete without manual certificate selection or manual vendor PIN entry.
- The native prompt receives at most one input batch and one submit per USB insertion cycle.
- The broker activates submit only after the exact PIN input control has processed the complete batch, without retrieving or recording the PIN text or character count.
- A fully matched background dialog is activated and focused before input, or the attempt fails closed without sending the PIN.
- A non-matching process, window, control tree, owner, focus, or signature receives no input.
- A successful target TLS handshake is required before fixed-PIN setup is considered validated.
- Release artifacts, tests, logs, and OpenSpec files contain no real PIN.
