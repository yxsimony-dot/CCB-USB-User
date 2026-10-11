# Validation — 2026-10-11

Initial regression run against the old service: 11 failures and 34 passing cases, including the API-quota failure, duplicate requests, checksum/redirect validation and ordinary HTTP 403 classification. Focused tests pass after implementation; final suite counts are recorded below after review.

Final local results: 60 focused update tests passed; full Release-configuration non-hardware .NET suite passed 1,726 tests with no failures or skipped cases. Independent source review found no release blocker; git diff --check and strict OpenSpec validation passed. The existing unrelated unused-event CS0067 warning remains. The main release workflow will also run browser, packaging and release tests before publishing.

Real system-network diagnostic on this machine returned HTTP 403, X-RateLimit-Remaining=0 from api.github.com while direct release-manifest download returned HTTP 200. No token or proxy change was used.

The smoke harness compiled the actual update-service sources, disallowed all api.github.com requests and used a normal HttpClientHandler with the system network settings. From installed version 1.1.4 it found release 1.1.5, followed version-specific GitHub/CDN assets, reused the second check without HTTP, downloaded all 81,152,390 installer bytes and passed VerifyDownloadedAsync. From installed version 1.1.5 it reported UpToDate and reused the second check. No installer or bank business was started.

The local synthetic harness and logs are ignored under artifacts/update-static-smoke. These network checks validate this machine's real network path, not connectivity from every other user's computer. Affected older clients still need one manual installation of the repair release.
