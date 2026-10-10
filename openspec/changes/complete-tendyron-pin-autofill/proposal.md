# Complete Tendyron Native PIN Autofill

## Why

The launcher already contains the fixed-PIN storage path, the Tendyron authentication configuration, and a restricted Native Prompt Broker. The remaining gap is end-to-end completion for the current Tendyron Key: the embedded WebView2 browser must load the official CCB UKey extension, automatically select the configured certificate, and use the stored PIN only when the target TLS private-key operation requires it.

Real-device diagnosis showed that ordinary Edge succeeds because it has the official CCB UKey extension installed, while the launcher's WebView2 environment had browser extensions disabled. This change closes that gap without weakening the existing extension identity, certificate target, window, process, publisher, integrity, owner-window, and single-attempt checks.

## Scope

- Reproduce and diagnose the current Tendyron native PIN prompt using the published application and broker.
- Verify the fixed-PIN setup flow writes only to the current Windows user's Credential Manager and creates a pending validation marker.
- Verify both insertion timings:
  - the Key is present before the launcher starts;
  - the Key is inserted while the launcher is already running.
- Correct only the necessary profile, window-matching, broker, focus, input, coordination, or release wiring needed for the current Tendyron prompt.
- Package the installed official CCB UKey extension and verify its manifest-derived ID, name, version, permissions, match pattern, and required files before use.
- Create WebView2 with browser-extension support and install or enable the verified extension before any navigation.
- Allow the configured navigation entry point and exact certificate-authentication TLS endpoint to be different HTTPS hosts while continuing to authorize client-certificate selection only for the exact configured TLS host and port.
- Rebuild, reinstall, and validate the self-contained release package against the current Tendyron Key and target TLS endpoint.
- Record rollback, broker uninstall, and local credential cleanup steps without recording the PIN.

## Non-goals

- Reading, exporting, displaying, logging, or transmitting the user's real PIN.
- Storing the PIN in JSON, source code, command-line arguments, environment variables, clipboard contents, or web content.
- Automating arbitrary password dialogs or bypassing an unmatched vendor prompt.
- Replacing the USB Key, certificate, CSP, driver, or vendor software.
- Expanding the feature to OTPs, transaction confirmation, CAPTCHA, or general browser automation.

## User-facing impact

 After the user enters the fixed PIN once through the launcher's local password dialog, the launcher opens the CCB corporate banking entry page with the official UKey extension available, automatically selects the configured Tendyron certificate for the explicit `b2b3.ccb.com:443` and `xc.b2b3.ccb.com:443` authentication endpoints, and fills and confirms a matching vendor password window during the authorized private-key operation. The user should no longer need to select the certificate or type the PIN into that native dialog. If the extension package, certificate request, dialog identity, or TLS flow does not match the authorized contract, the launcher must fail closed.
