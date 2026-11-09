# Tendyron Hardware Acceptance

Date: 2026-11-05

This record intentionally excludes PINs, certificate data, Token identities, and
device serial or instance identifiers.

## Environment

- Application version: `1.1.20`
- Platform: Windows x64
- Vendor family: Tendyron
- Public hardware family: `VID_1780&PID_0401`
- Detection mode: read-only

## Completed Checks

- SetupAPI mapped the Tendyron media evidence to the nearest physical USB
  device instead of an upstream USB hub.
- The real PSBC probe aggregator returned `Ready` for the connected Tendyron
  Key.
- The real USB monitor, PSBC device monitor, probe aggregator, and presence
  observer published `Ready` for a Key that was already connected.
- The main UI reached `Ready` after its bounded metadata/environment retry and
  displayed `可用邮储 Key 已就绪，点击启动业务开始`.
- With the Key still connected, switching to CCB displayed the CCB connected
  state without starting business, authentication, navigation, or PIN work.
- Switching back to PSBC without reinsertion returned to the PSBC `Ready`
  state and still did not start business work.
- The verification payload contained no known proprietary PSBC PowerAssist
  vendor DLLs.

## Automated Verification

- Focused PSBC, mapper, monitor, composition, and presentation tests: 104 passed.
- Complete non-hardware .NET test suite: 1824 passed.
- Browser test suite: 267 passed.
- Real connected-device detector and observer hardware tests: 2 passed.

## Pending Checks

- Controlled environment-unavailable acceptance.
- Physical removal while the application is running.
- Physical reinsertion and return to `Ready`.

OpenSpec task 5.2 remains incomplete until all pending checks above are
performed with the physical Key.
