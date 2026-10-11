## ADDED Requirements

### Requirement: Static stable release discovery
The client SHALL discover stable updates from the fixed public repository's latest release manifest without calling api.github.com on the normal successful path. It SHALL only offer versions numerically above the installed version. The release source, tag/version relationship, schema, product, architecture, minimum OS, clean source commit, protocol, installer name, size and SHA-256 SHALL be validated. Matching immutable SHA256SUMS SHALL agree with the manifest before download is offered.

#### Scenario: API quota exhausted but release assets accessible
- **WHEN** anonymous GitHub API requests are denied while the stable manifest and checksum assets remain accessible
- **THEN** a valid newer version is discovered without any API request

#### Scenario: Stable metadata is invalid
- **WHEN** manifest and selected release tag or checksum disagree
- **THEN** report an explicit failure and do not offer an installer or claim the client is up to date

### Requirement: Bounded caching and accurate failure reporting
The client SHALL reuse successful results for 60 seconds, honor cancellation, distinguish ordinary HTTP 403 from confirmed throttling, and avoid repeated requests during server-provided rate-limit cooldown.

#### Scenario: Repeated checks
- **WHEN** startup and manual checks run within the success-cache interval
- **THEN** reuse the checked result without another network request

#### Scenario: Quota or server throttle
- **WHEN** rate-limit evidence or HTTP 429 is returned
- **THEN** show rate limiting with retry guidance and suppress requests until cooldown expires

#### Scenario: Access denied
- **WHEN** HTTP 403 lacks rate-limit evidence
- **THEN** report access denial or a network issue without claiming quota exhaustion

### Requirement: Verified installation handoff
Downloads SHALL continue using approved HTTPS sources, cancellation, size limits and SHA-256 verification before installation is permitted. The update fix SHALL NOT start an installation or bank session automatically.

#### Scenario: Corrupt installer
- **WHEN** downloaded bytes fail size or hash verification
- **THEN** do not execute the file and retain the current application
