## Purpose

Let the user export the first current account's complete transaction statement for a selected month of the current year as the bank's original PDF on the Windows desktop, using the existing authenticated browser session.

## ADDED Requirements

### Requirement: Explicit monthly export
The system SHALL start export only when the user presses Export Statement. It SHALL use the current local year and selected month 1 through 12, select only the first available current account, and query the complete calendar month without changing other default conditions.

#### Scenario: Leap year February
- **WHEN** the user exports February in a leap year
- **THEN** the request covers February 1 through February 29

#### Scenario: Multiple accounts
- **WHEN** the account list contains multiple accounts
- **THEN** only the first account is selected for the statement query

#### Scenario: Future month or missing session
- **WHEN** the selected month is in the future or the bank session is unavailable
- **THEN** the operation reports an actionable status without submitting a statement query

### Requirement: Complete original PDF on desktop
The system SHALL invoke the bank's download-all PDF action, preserve the original PDF, and save it to the user's desktop without overwriting existing files. Success SHALL require a completed download and a PDF file signature.

#### Scenario: Download comes from a child window
- **WHEN** the verified PDF action requests a new bank window
- **THEN** export handles that download within the existing browser environment and reports success only after saving a verified PDF

#### Scenario: Ambiguous menu or invalid file
- **WHEN** download-all cannot be distinguished from current-page download or the result is not a PDF
- **THEN** the operation reports failure and does not claim a successful export

### Requirement: Isolated cancellable operation
The system SHALL prevent overlapping export operations, bound waits, and cancel on shutdown. It SHALL preserve the existing authentication and popup behavior and exclude secrets or transaction content from diagnostic output.

#### Scenario: Repeated click or closure
- **WHEN** export is already running or the app closes
- **THEN** another export does not start, pending work is cancelled on closure, and per-operation resources are released
