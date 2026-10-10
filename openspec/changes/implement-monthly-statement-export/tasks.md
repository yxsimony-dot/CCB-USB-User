## 1. Implementation

- [x] 1.1 Implement date range and PDF file handling with tests for leap years, future month, file validation and collision safety.
- [x] 1.2 Implement bank DOM adapter with JS tests for first account, dates, original handlers, menu scope, stale/foreign documents and no duplicate submission.
- [x] 1.3 Implement bounded workflow and same-session download/new-window handling; test phase transitions/cancellation and review resource lifetime.
- [x] 1.4 Bind independent UI command/status and shutdown cleanup; verify command availability and no login behavior regression.

## 2. Verification and delivery

- [x] 2.1 Pass full .NET/Node suites, independent review, OpenSpec validation and external release build.
- [x] 2.2 Update original closed application, verify hashes and unique entry, record deployment.
- [x] 2.3 Verify actual bank PDF reaches desktop for a selected month with the user; retain unverified status until confirmed.

## 3. Homepage entry regression

- [x] 3.1 Fix stale declared frame URL hiding the live bank homepage; reproduce captured b2bmainV7.jsp to LH navigation and distinguish missing entry from missing login.
- [x] 3.2 Validate, build and deploy the correction to the original closed application; retain live export acceptance as pending.

## 4. Floating PDF menu regression

- [x] 4.1 Support the bank's body-appended ccbDownload menu using its original hover events, prove owner by hidden-to-visible transition, preserve identity checks and prevent current-page export.
- [x] 4.2 Pass regression tests/review, publish and replace the original closed application; retain actual PDF acceptance as pending.

## 5. Attachment navigation event ordering

- [x] 5.1 Reproduce real WebView2 attachment event order locally and keep ConnectionAborted navigation alive for the following download event; retain terminal network/certificate errors.
- [x] 5.2 Validate, build and deploy the correction to the original closed application; retain actual bank PDF acceptance as pending.
