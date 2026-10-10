# Client Certificate Authentication

## ADDED Requirements

### Requirement: Verified official CCB UKey extension is available before navigation

The system MUST enable WebView2 browser-extension support and install or enable the packaged official CCB UKey extension before navigating to the configured CCB entry page.

#### Scenario: Packaged official extension is valid

- GIVEN the packaged extension has the expected manifest-derived ID `ahjacjkbgeefngpfnfhccehkfnaeahbf`
- AND its name, version, permissions, CCB match pattern, scripts, and icons match the authorized package
- WHEN the embedded browser initializes
- THEN the system installs or enables the extension before starting navigation

#### Scenario: Packaged extension is missing or does not match

- GIVEN the packaged extension directory, manifest, required file, version, name, permission, match pattern, or derived ID is missing or unexpected
- WHEN the embedded browser initializes
- THEN initialization fails before navigation
- AND no client certificate or PIN is exposed

### Requirement: Navigation and certificate TLS endpoints may be distinct

The system MUST support opening the configured CCB entry page on one HTTPS host while authorizing client-certificate selection only for a separately configured exact HTTPS host and port.

#### Scenario: CCB entry page reaches the authorized certificate endpoint

- GIVEN the navigation entry point is `b2bbank.ccb.com`
 - AND the exact certificate endpoint is `b2b3.ccb.com:443`
- WHEN WebView2 receives a client-certificate request from the exact configured endpoint
- THEN the system may select the exact configured certificate after all token and attempt checks pass

#### Scenario: Another host requests a client certificate

- GIVEN the launcher is displaying the CCB flow
- WHEN any host or port other than the exact configured certificate endpoint requests a client certificate
- THEN the request is denied
- AND the PIN and certificate are not committed to that request

### Requirement: Authorized Tendyron native prompt autofill

After the user has configured a fixed PIN through the local launcher UI, the system MUST automatically fill and submit the PIN only to the exact authorized Tendyron vendor password dialog that is associated with the current launcher authentication attempt.

#### Scenario: Matching vendor dialog appears during target TLS

- GIVEN the current Tendyron Key has been accepted for the active authentication cycle
- AND the exact client certificate has been selected for the configured target host
- AND target TLS is waiting for the token operation
- AND the broker verifies the authorized process, publisher, window, owner/root relationship, controls, and attempt ID
- WHEN the vendor password dialog appears
- THEN the broker establishes the exact dialog as the system foreground window
- AND establishes the exact configured PIN input control as the focused control
- AND revalidates the complete dialog identity, foreground window, and focused control
- AND sends one PIN input batch
- AND verifies without reading the PIN text that the exact input control has processed exactly the configured PIN character count
- AND waits for and revalidates the exact configured submit control
- AND activates that submit control once
- AND the launcher continues the target TLS flow

#### Scenario: Matching vendor dialog initially appears in the background

- GIVEN exactly one vendor password dialog matches the authorized process, publisher, window, owner/root relationship, controls, and attempt ID
- AND another application owns the system foreground when the dialog appears
- WHEN the broker performs its bounded target-only activation attempt
- THEN it activates only the matched dialog and focuses only the configured PIN input control
- AND it sends the PIN only after revalidating the dialog identity, system foreground window, and focused control
- AND it uses the remaining authentication-attempt timeout for PIN input and submit

#### Scenario: Matching dialog cannot be activated or focused

- GIVEN a vendor password dialog otherwise matches the authorized identity
- WHEN the broker cannot establish the exact dialog as the system foreground window
- OR cannot establish the exact configured PIN input control as the focused control
- OR the foreground window, focused control, or dialog identity changes during revalidation
- THEN the broker sends no PIN input
- AND the current authentication cycle fails closed without a formal validation stamp

#### Scenario: PIN input is not fully processed before submit

- GIVEN the exact authorized vendor password dialog has received one PIN input batch
- WHEN the exact PIN input control does not report exactly the configured PIN character count before the remaining authentication timeout
- OR the input control cannot be revalidated
- OR the input control reports unexpected pre-existing or excess characters
- THEN the broker does not activate the submit control
- AND the current authentication cycle fails closed without a formal validation stamp
- AND no PIN text or PIN character count is written to diagnostics

#### Scenario: Dialog identity does not match

- GIVEN a vendor-like password dialog is visible
- WHEN any authorized process, publisher, window, owner, control, focus, or attempt condition does not match
- THEN the broker sends no PIN input
- AND the current authentication cycle fails closed without a formal validation stamp

### Requirement: Validation depends on target TLS success

The system MUST distinguish a PIN being configured from the current certificate, token, native prompt, and target TLS flow being validated.

#### Scenario: Target TLS succeeds after automatic prompt confirmation

- GIVEN the fixed PIN has been saved locally
- AND the authorized token and vendor prompt flow succeeds
- WHEN the configured target TLS handshake completes successfully
- THEN the pending validation marker is upgraded to the formal validation stamp

#### Scenario: Native prompt or TLS flow fails

- GIVEN the fixed PIN has been saved locally
- WHEN the native prompt, broker, token operation, certificate selection, or target TLS flow fails
- THEN the formal validation stamp is not written
- AND the system does not replay the PIN within the same USB insertion cycle

### Requirement: Both USB insertion timings are supported

The system MUST support the current Tendyron Key being present before launcher startup and being inserted after the launcher is already running.

#### Scenario: Key is present before startup

- GIVEN the current Tendyron Key is inserted before the launcher starts
- WHEN the launcher initializes
- THEN it detects the Key, starts one authentication cycle, selects the configured certificate, and uses the authorized native prompt continuation when required

#### Scenario: Key is inserted after startup

- GIVEN the launcher is running without the current Tendyron Key
- WHEN the user inserts the Key
- THEN the launcher starts one new authentication cycle and follows the same certificate, token, prompt, and TLS rules without requiring manual certificate or vendor PIN entry

### Requirement: Fixed PIN remains local-only

The system MUST keep first-time PIN entry in the local password control and MUST NOT place the PIN in configuration, source, logs, command-line arguments, environment variables, clipboard contents, or web content.

#### Scenario: User configures or replaces the fixed PIN

- GIVEN the user opens the local fixed-PIN setup dialog
- WHEN the user confirms a PIN
- THEN the PIN is validated and stored only through the current Windows user's protected credential store
- AND the input control and transient buffers are cleared
- AND no PIN value is written to ordinary application artifacts
