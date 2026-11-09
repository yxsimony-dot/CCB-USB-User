## 1. Detection Contracts And Configuration

- [x] 1.1 Add the bank-neutral USB Key detection result, readiness status, vendor-family identity, diagnostic code, and physical-instance fields; verify unit tests cover `Absent`, `PresentButEnvironmentUnavailable`, and `Ready`.
- [x] 1.2 Add the `psbcUsbKey` profile strategy and validation rules without reusing CCB authentication settings; verify profile JSON round-trip and validator tests pass for valid, missing, and unknown strategies.
- [x] 1.3 Add injectable contracts for PSBC vendor probes, environment queries, and time-bounded probe execution; verify contract tests prove cancellation and exceptions cannot escape into the UI thread.

## 2. Windows Device And Environment Evidence

- [x] 2.1 Extend the Windows device snapshot model only with evidence required by the PSBC probes while preserving same-physical-device ancestry; verify mapper tests reject cross-device CD-ROM and volume evidence.
- [x] 2.2 Generalize USB metadata retry policy so the active bank strategy controls retry readiness instead of a CCB-only boolean; verify monitor tests cover delayed metadata, retry exhaustion, cancellation, and real removal.
- [x] 2.3 Implement read-only Windows queries for service state, registered CSP/provider presence, registry markers, and current Token evidence; verify tests use fakes and confirm no initialize, PIN, signing, or mutation operation is exposed.
- [x] 2.4 Add sanitized probe diagnostics that exclude PINs, certificate private data, and Token identities from ordinary logs; verify diagnostic serialization tests contain only vendor, status, and stable error codes.

## 3. PSBC Vendor Adapters

- [x] 3.1 Implement the PSBC probe aggregator with physical-device deduplication and independent vendor adapters; verify tests cover no candidates, one Ready adapter, environment-unavailable candidates, multiple candidates, and adapter failure isolation.
- [x] 3.2 Implement the Feitian/EnterSafe adapter using its device-family and PSBC provider evidence; verify offline fixtures cover absent, environment unavailable, and Ready outcomes.
- [x] 3.3 Implement the HDZB adapter using its known hardware, `HDZB_Service_PSBC`, provider, and marker evidence; verify offline fixtures cover absent, environment unavailable, and Ready outcomes.
- [x] 3.4 Implement the Tendyron adapter using `VID_1780&PID_0401`, same-device `TENDYRON U-DISK`/`PSBC` evidence, and PSBC OnKey environment checks; verify a fixture captured from the current Key returns the Tendyron family and Ready when its environment is available.
- [x] 3.5 Implement the Excelsecu adapter using its device descriptions and PSBC provider evidence; verify offline fixtures cover absent, environment unavailable, and Ready outcomes.
- [x] 3.6 Implement the WatchData adapter using its vendor probe and `Watchdata PSBC CSP` evidence; verify offline fixtures cover absent, environment unavailable, and Ready outcomes.
- [x] 3.7 Add regression tests proving installed vendor software without a connected Token never produces Ready and proving ordinary refresh never invokes `TokenInitialize`-class behavior.

## 4. Selected-Bank Runtime Integration

- [x] 4.1 Add an independent PSBC profile and configure the postal bank flow to construct only PSBC detection/runtime dependencies; verify selecting PSBC no longer shows the unconfigured placeholder and does not read the CCB profile.
- [x] 4.2 Refactor bank flow composition so each flow owns its matcher, retry policy, presence observer, authentication runtime, browser session, and commands; verify composition tests prove only the selected bank branch is constructed and subscribed.
- [x] 4.3 Map PSBC detection states into the existing presentation model with distinct absent, environment-unavailable, and ready messages; verify view-model tests cover each state without exposing sensitive diagnostic values.
- [x] 4.4 Preserve overlapping matching semantics so one device can independently match CCB and PSBC; verify tests using `VID_1780&PID_0401` run only CCB behavior when CCB is selected and only PSBC behavior when PSBC is selected.
- [x] 4.5 Harden bank switching so the previous session, observer, runtime, and commands are disposed before the new flow publishes state; verify delayed callbacks from the previous bank cannot update USB, authentication, browser, or business presentation.
- [x] 4.6 Verify switching banks while a Key remains connected initializes the new bank from a fresh snapshot without requiring physical reinsertion or creating duplicate business-start attempts.

## 5. Verification And Hardware Acceptance

- [x] 5.1 Run focused Core, Windows, composition, presentation, profile, and USB monitor tests; verify all new PSBC and bank-isolation tests pass.
- [ ] 5.2 Perform read-only hardware acceptance with the current Tendyron PSBC Key for insertion, environment unavailable, Ready, bank overlap, bank switching, removal, and reinsertion; record a sanitized result artifact with no PIN or Token identity.
- [ ] 5.3 Capture sanitized fixtures and complete the same hardware acceptance for Feitian, HDZB, Excelsecu, and WatchData when samples are available; keep each vendor explicitly marked unverified until its real-device checks pass.
- [x] 5.4 Run the complete test suite and Release x64 build; verify there are no failures, no new warnings, and the release payload does not contain copied PSBC PowerAssist proprietary DLLs.
- [x] 5.5 Manually smoke-test the main bank selector with an overlapping Key; verify the inactive bank performs no authentication, navigation, PIN action, or business command before approving implementation completion.
