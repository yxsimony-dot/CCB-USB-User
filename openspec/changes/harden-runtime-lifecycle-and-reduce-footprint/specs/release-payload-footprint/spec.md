## Purpose

Define a measurable release-payload reduction contract that removes unsupported localization and inactive diagnostics while preserving the launcher's self-contained Windows runtime, security boundaries, and bank workflow compatibility.

## ADDED Requirements

### Requirement: Release payload includes only supported satellite languages

The release build SHALL include satellite resources only for languages explicitly supported by the product. The current supported satellite language is `zh-Hans`; framework or package satellite directories for other cultures MUST NOT be shipped.

#### Scenario: Build the production win-x64 payload
- **WHEN** the release entry point publishes the application and broker
- **THEN** the payload contains the `zh-Hans` satellite resources required by runtime dependencies
- **AND** it does not contain satellite resource directories for unsupported cultures

### Requirement: Footprint reduction preserves deployment compatibility

The application SHALL remain self-contained for `win-x64`, SHALL NOT enable trimming, and SHALL retain every runtime, WebView2, browser-extension, profile, installer, and broker asset required by the validated deployment contract. Runtime files MUST NOT be removed through an ad hoc filename deletion list.

#### Scenario: Validate a reduced payload
- **WHEN** the release payload is validated after footprint changes
- **THEN** the main application starts without an installed .NET runtime
- **AND** WebView2 initialization, extension loading, profile loading, update handoff, and broker packaging retain their required files
- **AND** payload validation rejects a release missing a declared required asset

#### Scenario: Publish configuration is inspected
- **WHEN** the production publish properties are evaluated
- **THEN** self-contained deployment is enabled
- **AND** trimming is disabled
- **AND** any size reduction comes from supported publish options or removal of confirmed unused product assets

### Requirement: Single-file publishing is compatibility gated

The release process MAY enable single-file publishing for the main application only after automated payload checks and startup smoke tests prove that WPF, WebView2, embedded scripts, external extension files, configuration, signing, installer binding, and update handoff continue to work. If any gate fails, the release SHALL retain the conventional self-contained layout.

#### Scenario: Single-file candidate passes every gate
- **WHEN** a main-application single-file candidate passes the complete release and startup compatibility suite
- **THEN** the release configuration may adopt that candidate
- **AND** all intentionally external assets remain present and addressable

#### Scenario: Single-file candidate fails a gate
- **WHEN** any required runtime, packaging, signing, startup, or integration gate fails
- **THEN** production publishing remains on the validated conventional self-contained layout
- **AND** the measured result and rejection reason are recorded without weakening the gate

### Requirement: Inactive diagnostics are absent from production

The production application and payload SHALL omit diagnostic classes, scripts, embedded resources, and tests that have no production construction or invocation path. The user-invoked diagnostic bundle exporter and active bounded lifecycle diagnostics SHALL remain available.

#### Scenario: Inspect production diagnostic assets
- **WHEN** the application project and payload are inspected after the reduction
- **THEN** the unused popup and statement sampling chain is absent
- **AND** the diagnostic bundle export command remains functional
- **AND** no bank workflow behavior depends on the removed assets

### Requirement: Payload size and file count are regression checked

The release build SHALL record payload file count and unpacked byte size and SHALL compare them with committed bounds established from the first accepted reduced build. An unexplained increase beyond those bounds MUST fail validation or require an explicit reviewed baseline update.

#### Scenario: Build remains within the accepted footprint
- **WHEN** a release payload is generated
- **THEN** its file count and unpacked size are written to machine-readable build metadata
- **AND** validation succeeds when both values remain within the committed bounds

#### Scenario: Payload grows beyond the accepted footprint
- **WHEN** a dependency or project change increases file count or unpacked size beyond the committed bounds
- **THEN** release validation fails with the measured difference
- **AND** updating the bounds requires an explicit source change that explains the added runtime value

