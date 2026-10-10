## 1. Diagnostic implementation

- [x] 1.1 Add bounded read-only statement page snapshots; verify JS regressions cover forms, legacy frames, redaction and no page mutation.
- [x] 1.2 Add sanitized request/download metadata and bounded local logging; verify .NET redaction and lifetime tests.
- [x] 1.3 Integrate independent diagnostic initialization/disposal; verify Release build and review event handlers for no request or download mutation.

## 2. Delivery and evidence

- [x] 2.1 Run relevant full regression suites, publish externally, and verify changed release hashes and OpenSpec validation.
- [x] 2.2 Update original entry after app closure; verify deployed hashes, unique executable and manifest.
- [x] 2.3 Inspect one user-performed query/PDF download and document evidence-based export integration recommendation; do not mark complete before actual capture.
