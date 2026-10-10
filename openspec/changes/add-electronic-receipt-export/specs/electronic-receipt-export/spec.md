## Purpose

Allow users to export the first page of electronic receipt query results for a selected calendar month from their existing authenticated bank session. Save the bank-generated PDF to the shared download directory with an account-based receipt filename, without processing later result pages.

## ADDED Requirements

### Requirement: Explicit receipt export command with shared settings
The application SHALL provide a “导出电子回单” button beside the statement export action and SHALL reuse the existing month selector and download directory. It SHALL capture the selected month, local current year and directory when the operation starts. It SHALL NOT start receipt export merely because the application opens, login succeeds or the selection changes.

#### Scenario: Export starts only on explicit click
- **WHEN** a logged-in user selects a month and clicks “导出电子回单”
- **THEN** one receipt export starts using the selected month and shared directory

#### Scenario: Settings changes without export
- **WHEN** login completes or the month selection changes without an export click
- **THEN** no receipt query or download is triggered

### Requirement: Fresh homepage and monthly receipt query
The operation SHALL first confirm the bank homepage is ready, navigating home when necessary, then open “账户查询 → 电子回单查询”. It SHALL use the same first current account targeted by statement export and query the first through last calendar day of the selected month in the captured year. It SHALL use batch receipt query, all print statuses and transaction directions, empty optional amount and voucher filters, PDF format and merged download enabled. It SHALL reject future months without submitting a query and SHALL NOT silently shorten the current month range.

#### Scenario: Start from another business tab
- **WHEN** the active bank tab is not the homepage
- **THEN** the operation returns home and confirms readiness before opening receipt query

#### Scenario: Whole month and matching account
- **WHEN** the user exports September of the current year
- **THEN** the query uses September 1 through September 30 for the same first current account used by statement export and the prescribed receipt filters

#### Scenario: Future month
- **WHEN** the selected month is later than the current local month
- **THEN** the operation explains that the month has not arrived and does not submit a bank query

### Requirement: Export only the first result page
The operation SHALL confirm the results correspond to its account, date range and fresh query and are on page one. It SHALL ensure all selectable receipts on that page are selected and activate “电子回单预览/打印” exactly once. It SHALL NOT navigate to later pages or use “下载全部”. Existing selection SHALL NOT be toggled off. It SHALL report empty results without invoking preview/print or creating a PDF.

#### Scenario: Multiple result pages
- **WHEN** a matching fresh query has more than one result page
- **THEN** only page one is selected and exported, and no later page is visited or downloaded

#### Scenario: All first-page rows already selected
- **WHEN** every selectable receipt on the verified first page is already selected
- **THEN** the operation preserves that selection and activates preview/print once

#### Scenario: Stale or non-first-page results
- **WHEN** an old tab shows results for a previous query or the active result page is not page one
- **THEN** those results are not printed and the operation waits for its valid fresh results or reports that export could not be completed

#### Scenario: No receipts
- **WHEN** the bank reports no receipts for the requested account and month
- **THEN** the application reports no matching receipts and creates no PDF

### Requirement: Preserve and name the downloaded bank PDF
The application SHALL save only the PDF caused by the current receipt preview/print action to the captured download directory, without altering its contents. The receipt filename SHALL be `月份-公司全名-建行账号后4位-回单.pdf`, and the statement filename SHALL be `月份-公司全名-建行账号后4位-对账.pdf`. Both SHALL prefix the operation's captured month as two digits `01` through `12`, with zero padding and no `月` character, and SHALL use ASCII hyphens between the four filename segments. This final user requirement supersedes the earlier requirements to omit the month and preserve statement filenames. Company and account identity SHALL come from the verified queried account, retaining leading zeroes in the last four digits. Invalid filename characters SHALL be sanitized; an identity that cannot be safely represented without truncating the company name SHALL stop export. A collision SHALL append a numeric suffix before `.pdf` and SHALL NOT overwrite any existing file. Existing files SHALL NOT be renamed. Success SHALL require completed download and PDF validation.

#### Scenario: Valid receipt download
- **WHEN** the matching bank PDF download completes and passes validation
- **THEN** it is saved under the verified account's receipt filename and the application reports its final path

#### Scenario: Filename collision
- **WHEN** `09-示例公司-建行0507-回单.pdf` already exists for the selected September export
- **THEN** the new file receives an available name such as `09-示例公司-建行0507-回单 (2).pdf`, preserving the existing file

#### Scenario: Statement filename uses the revised convention
- **WHEN** a September statement PDF for the verified example company and account ending in 0507 completes
- **THEN** its filename is `09-示例公司-建行0507-对账.pdf`, or an available collision name such as `09-示例公司-建行0507-对账 (2).pdf`, without renaming older exports

#### Scenario: Filename retains the operation's month
- **WHEN** an export started for September completes after the UI selection or calendar month has changed
- **THEN** its filename still starts with `09-`, using the month captured for that export

#### Scenario: Unrelated download or invalid file
- **WHEN** an unrelated download occurs or the expected download is not a valid completed PDF
- **THEN** it is not renamed as the current receipt export and receipt success is not reported

### Requirement: Shared export exclusion and bounded cleanup
Statement and receipt export SHALL share exclusive access to the bank workflow. While either operation is active, repeated or cross-action clicks SHALL NOT start or queue another export. Conflicting settings and preparation actions SHALL remain disabled consistently with existing busy behavior. Completion, failure, cancellation and shutdown SHALL release the active operation's listeners, temporary files and resources before accepting another export. Failed or timed-out query and print actions SHALL NOT be automatically resubmitted.

#### Scenario: Cross-action clicks
- **WHEN** receipt export is running and the user invokes either export button again
- **THEN** no second query, selection or download is started or queued

#### Scenario: Retry after completion or failure
- **WHEN** an operation has completed cleanup and the user starts a new export
- **THEN** the new operation uses a fresh query and its own settings and does not reuse stale selections or downloads

#### Scenario: Shutdown during download
- **WHEN** the application closes with an export in progress
- **THEN** the export is cancelled and its incomplete file and download resources are cleaned up without altering existing PDFs

### Requirement: Accurate progress and compatibility
The application SHALL distinguish receipt progress from statement progress and SHALL make clear that only the first receipt result page is exported. It SHALL report unsupported pages, mismatched results, session expiry and timeout as incomplete operations rather than successes. It SHALL preserve full-statement export behavior, manual login/PIN behavior and the shared directory preference while applying the revised filenames defined above to new exports. Diagnostics SHALL NOT record credentials, full account identifiers, transaction bodies or receipt contents.

#### Scenario: First-page success message
- **WHEN** the first page is exported from a multi-page result
- **THEN** the message identifies receipt export of the first page and does not claim all receipts for the month were exported

#### Scenario: Alternating export types
- **WHEN** a statement export follows a completed receipt export
- **THEN** the statement retains its existing download scope and uses `月份-公司全名-建行账号后4位-对账.pdf` with that operation's month rather than the receipt filename
