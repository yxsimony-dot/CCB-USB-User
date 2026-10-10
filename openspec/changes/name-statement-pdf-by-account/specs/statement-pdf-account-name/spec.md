## ADDED Requirements

### Requirement: Account-specific PDF filename
The system SHALL name exported PDF files using the complete company name, the literal 建行, and the last four digits of the exported account, followed by .pdf. It SHALL preserve leading zeros and SHALL NOT append the selected month to the base name.

#### Scenario: Complete name and leading zero
- **WHEN** a verified company name includes a branch or union suffix and the account suffix starts with zero
- **THEN** the file retains the full name and all four account suffix digits

### Requirement: Verified naming identity
The system SHALL obtain naming identity from the current bank page and verify that it belongs to the account queried in this export. It SHALL stop if the identity is missing, ambiguous, truncated or inconsistent.

#### Scenario: Identity changes before PDF action
- **WHEN** the page's company or account changes after naming identification
- **THEN** no PDF download is initiated for the stale identity

### Requirement: Preserve existing files
The system SHALL avoid overwriting existing output files and SHALL replace filename-invalid characters without silently truncating the company name.

#### Scenario: Same account exported again
- **WHEN** the target base filename already exists
- **THEN** a numbered suffix is used and the existing PDF is preserved
