## MODIFIED Requirements

### Requirement: PIN preparation remains independent

The application SHALL allow the user to set a PIN while business is idle, using the existing protected credential storage and pending-validation model. PIN setup SHALL use a masked local password control, transfer the value through short-lived clearable secret buffers, and MUST NOT materialize the PIN as an ordinary managed string. Saving, replacing, cancelling, or failing PIN setup SHALL clear the input control and every transient buffer. PIN preparation MUST NOT implicitly install prerequisites, start business, unlock the token, navigate, submit a vendor prompt, or report authentication success.

#### Scenario: Environment is unavailable
- **WHEN** the user selects PIN setup and the existing authentication prerequisite check fails
- **THEN** the application directs the user to the environment-install action
- **AND** it does not implicitly perform installation or business activity

#### Scenario: Save PIN before starting business
- **WHEN** the user successfully saves a PIN while business is idle
- **THEN** the credential is stored through the current protected credential service and remains pending validation
- **AND** the application reports that the PIN was saved and waits for an explicit business start
- **AND** it does not read the credential for unlock, navigate, or produce an authentication-success marker

#### Scenario: Change PIN after business has previously started
- **WHEN** the user has stopped the previous business session and successfully replaces the PIN
- **THEN** the new credential is not automatically used by later device notifications
- **AND** only a new explicit business start applies the pending credential change

#### Scenario: Delete a saved but unapplied PIN
- **WHEN** the user saves a PIN before business starts and then deletes the local PIN
- **THEN** the stored credential and validation markers are removed
- **AND** a later start does not replay the deleted credential or a previous save event

#### Scenario: Confirm PIN in the local dialog
- **WHEN** the user enters and confirms a PIN
- **THEN** the characters are masked on screen
- **AND** the dialog copies them from the password control into a clearable PIN buffer without creating an ordinary managed PIN string
- **AND** ownership of that buffer is transferred exactly once to the save operation

#### Scenario: Cancel or fail PIN setup
- **WHEN** the user cancels the dialog, cancellation is requested, validation fails, saving throws, or the dialog closes unexpectedly
- **THEN** the password control and all transient PIN buffers created by the setup flow are cleared or disposed
- **AND** no PIN value is written to logs, configuration, clipboard, command-line arguments, environment variables, or page content

