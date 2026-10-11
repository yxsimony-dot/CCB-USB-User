## Why

Other clients cannot check updates because anonymous GitHub API requests share a 60/hour network-exit quota. The same system network reproduced HTTP 403 with X-RateLimit-Remaining=0 while direct release-manifest download succeeded. Existing code also labels all 403 responses as rate limiting.

## What Changes

- Prefer the public stable release manifest and matching SHA256SUMS without GitHub API calls.
- Cache successful checks for 60 seconds and respect rate-limit cooldown with precise errors.
- Retain trusted HTTPS sources, manifest validation, numeric upgrade comparison, installer byte count and SHA-256 verification.
- Provide a new installer for affected older clients to install manually once.

## Capabilities

### New Capabilities
- `static-release-update-check`: static release discovery, integrity, caching and failure handling.

### Modified Capabilities
None. This correction supersedes the unarchived add-github-client-updates client discovery design, leaving its historical validation record intact.

## Impact

App update service and tests; update documentation. No bank automation, credentials, installer transaction or release version scheme changes.
