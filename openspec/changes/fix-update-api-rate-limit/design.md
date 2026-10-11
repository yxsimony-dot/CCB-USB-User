## Decision

Use the fixed repository's /releases/latest/download/release-manifest.json as the primary stable channel. Follow only approved HTTPS hosts and bind the returned manifest version to the immutable release tag encountered during redirects. Read matching version-specific SHA256SUMS, validate its installer filename/hash against the manifest and construct the immutable installer URL. CI continues verifying tag/source identity and uploaded assets before making a release public. The normal client path no longer re-queries Git refs. Integrity hashes are not a publisher signature.

Keep the existing bounded API discovery only as a compatibility fallback when the direct manifest returns 404. Do not fallback on forbidden, rate limit, malformed metadata or network errors. An invalid current stable release is an explicit failure rather than silently searching older candidates. Numeric comparison prevents downgrades. Existing publishing retains make_latest:legacy with monotonic version/run checks; this change does not introduce another mutable update feed.

Cache successful in-memory check results for 60 seconds. Cancellation takes precedence over cached results. Detect actual rate evidence (429 or explicit remaining=0/retry headers), honor server retry/reset time and show when retry is allowed. Plain 403 means access denied rather than confirmed quota exhaustion. No tokens, proxy changes or automatic retry loops.

## Validation

Test zero API calls on the primary path, cached check/expiry/cancellation, malformed or mismatched manifest/checksum, forbidden redirects, real rate limits and cooldown, generic access denial, and download integrity. Run the actual service against public assets through the unchanged system network, including a second cached check and verified installer download without execution. Run the existing non-hardware/build/release gates before delivering the published installer.
