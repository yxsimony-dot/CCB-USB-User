## Purpose

Provide bounded local technical evidence of a manually performed current-account statement query and PDF download so the monthly export can later reuse the authenticated bank workflow without screen-coordinate automation.

## ADDED Requirements

### Requirement: Read-only statement discovery
The system SHALL observe bank page structures and network/download metadata during the user's manual workflow without navigating, submitting, altering requests or changing downloads.

#### Scenario: Manual query and PDF download
- **WHEN** the user selects an account, enters dates and downloads a PDF
- **THEN** diagnostic records describe available control/form structures and corresponding network/download events while original operations retain their behavior

### Requirement: Limited local metadata
The system SHALL exclude credentials, cookies, arbitrary page text, input values, account numbers, query parameter values and response bodies from diagnostic output. Page snapshots SHALL be restricted to allowed bank documents and accessible same-origin descendants.

#### Scenario: Sensitive values are present
- **WHEN** inputs, URLs and page content contain account identifiers or session values
- **THEN** output contains only sanitized structural metadata and fixed public workflow labels

#### Scenario: An unrelated frame is present
- **WHEN** a frame is outside the allowed bank origin
- **THEN** its contents are not inspected

### Requirement: Bounded independent lifetime
The system SHALL bound sampling, writes and diagnostic duration; failures SHALL NOT block login or business operations. Repeated identical page snapshots SHALL NOT be logged repeatedly.

#### Scenario: Limits or failures occur
- **WHEN** diagnostics reaches its time or file limit, a snapshot overlaps, or a read/write fails
- **THEN** it stops or skips diagnostic work without changing the browser's business state
