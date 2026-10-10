## Purpose

Provide one deliberate monthly export action that saves the bank statement before exporting the first page of electronic receipts, while preserving empty-result, failure, cancellation and partial-success outcomes without losing completed files.

## ADDED Requirements

### Requirement: One explicit combined export action
The application SHALL replace the two separate export buttons with one button labelled “导出流水和回单”. It SHALL start only after an explicit click and use one captured current local year, selected month and download directory for both stages. It SHALL preserve existing whole-month date selection and reject future months before querying.

#### Scenario: One click fixes the settings for both documents
- **WHEN** the user selects a month and clicks “导出流水和回单”
- **THEN** one combined operation starts with that year, month and directory, and both documents use those settings even if the clock or stored preferences subsequently change

#### Scenario: Opening the app does not export
- **WHEN** the app opens, login completes or the month selection changes without the export click
- **THEN** neither a statement nor receipt export starts

### Requirement: Statement completion precedes receipt navigation
The application SHALL export the statement first and SHALL wait for its completed PDF save and resource cleanup before returning home to begin the existing first-page receipt flow. It SHALL continue to receipts only when the statement stage reports success. Failure, cancellation and verified empty statements SHALL stop the combined operation without starting receipt navigation or download.

#### Scenario: Successful statement followed by receipts
- **WHEN** the statement PDF is saved successfully and statement cleanup finishes
- **THEN** the application returns home and exports only the first page of receipts for the same captured month

#### Scenario: Statement download or cleanup is still pending
- **WHEN** the statement file is downloading or its cleanup has not completed
- **THEN** no receipt navigation, query or download starts

#### Scenario: Statement fails
- **WHEN** the statement stage fails or is cancelled
- **THEN** the application reports that outcome and does not start the receipt stage

### Requirement: Verified empty statements stop on the statement page
The application SHALL recognize empty statements only from affirmative evidence tied to the current query. It SHALL NOT classify timeout, unrecognized pages, expired sessions, stale results or temporarily missing controls as empty. On verified empty statements it SHALL display “流水页面为空，不需要导出该月份流水。”, preserve the current statement tab, create no empty PDF and stop without returning home or querying receipts.

#### Scenario: Current query explicitly has no statement entries
- **WHEN** the matched current query displays a verified empty statement result
- **THEN** the application displays the exact empty-statement message, remains on that statement page and creates neither a statement PDF nor a receipt query

#### Scenario: Results cannot be established
- **WHEN** the statement page is unsupported, still loading, stale or timed out without affirmative empty-result evidence
- **THEN** the application reports or waits according to that condition and does not claim the statement is empty

### Requirement: Distinct outcomes and retained partial results
The application SHALL distinguish success, empty result, failure and cancellation for each stage. Successful stages SHALL expose their completed file paths. If receipts fail, are cancelled or are empty after statement success, the application SHALL retain the saved statement and its path and SHALL clearly report the receipt outcome without claiming both PDFs were saved.

#### Scenario: Both files succeed
- **WHEN** the statement and receipt stages both save valid completed PDFs
- **THEN** the application reports both completed paths

#### Scenario: Receipt failure after statement success
- **WHEN** the receipt stage fails or is cancelled after the statement PDF was saved
- **THEN** the statement file and its path remain available and the message identifies the partially completed operation and unfinished receipt stage

#### Scenario: No receipts after statement success
- **WHEN** the receipt query has a verified empty result after statement success
- **THEN** the application reports that the statement was saved and that the month has no receipts, without creating an empty receipt PDF

### Requirement: Exclusive operation through final cleanup
The application SHALL keep one busy state throughout both stages, their transition and all cleanup. It SHALL ignore repeated clicks without queuing another run and disable conflicting controls consistently with existing export behavior. Closing the app SHALL cancel the active stage and wait for cleanup before destroying the browser; cancellation SHALL prevent any later-stage navigation while preserving completed files.

#### Scenario: Click during stage transition
- **WHEN** the user clicks again after statement completion while receipt startup or cleanup is pending
- **THEN** no second operation is started or queued and conflicting settings remain disabled

#### Scenario: Close at the stage boundary
- **WHEN** the application is closed after the statement finishes but before receipts begin
- **THEN** the completed statement is preserved, receipts do not navigate or download, and resources are cleaned before browser shutdown

### Requirement: Existing naming and authentication remain compatible
The combined operation SHALL retain the existing zero-padded `01` through `12` month naming, company name sanitization, account suffix leading zeroes, `对账` and `回单` document suffixes, collision numbering and no-overwrite PDF saving. It SHALL retain first-page-only receipt scope and existing PIN and authentication behavior.

#### Scenario: January combined export
- **WHEN** January documents are exported for a verified account ending in `0507`
- **THEN** the files follow `01-公司全名-建行0507-对账.pdf` and `01-公司全名-建行0507-回单.pdf`, with collision suffixes when needed and no overwrite of existing files

#### Scenario: Receipt result contains more than one page
- **WHEN** the receipt stage obtains multiple result pages
- **THEN** it exports only page one and does not visit or download later pages
