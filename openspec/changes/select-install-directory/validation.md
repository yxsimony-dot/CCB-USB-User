# Validation — 2026-10-10

Final delivery: main commit 682e7a239bfa675386968a9bf0eca31d066df623 passed GitHub Actions run 38061042810 and published v1.1.4. Downloaded the public release into artifacts/releases/1.1.4; verified installer file version 1.1.4.0, size 81,147,445 bytes, matching manifest/source commit and SHA256SUMS, SHA-256 f03493804ffa7221042b4671a5a51f1b2504c0ffec388782da400b3e14bf2afa. This post-release evidence is retained locally to avoid a documentation-only main push publishing another installer.

The installer now defines both `{userdesktop}` and `{userprograms}` shortcuts for the current Windows user. The packaging contract test checks both entries, and the finished-page text points users to either shortcut.

Full clean-source build of f2f1d892a908d9b3055c0ca1e209127fb448086f passed: 1,701 non-hardware .NET tests, 292 browser tests, 14 release tests, all packaging/directory/transaction suites, and validation of 488 payload files. The generated local preview installer matches its manifest size and SHA-256. Windows PowerShell 5.1 directory and transaction suites also passed separately.

The actual installer opened on the Chinese directory selection page, accepted an F-drive subdirectory containing Chinese characters and spaces, then advanced to the ready page. The preview was cancelled before installation. No application directory, transaction, broker registration or dependency installation was created. Remaining English browse/disk-space strings were translated and the installer recompiled successfully; the localized directory page and ready page were inspected again and cancelled.

Upgrade detection, pending custom-directory recovery, forbidden paths, junction ancestry, personal-file protection and manifest-scoped uninstall are covered by synthetic tests. Full installation, UAC approval, real upgrade/uninstall and clean-machine bank-driver acceptance were not performed in this verification. Local preview version 1.1.0 is for wizard validation only; final delivery uses the existing main-triggered release pipeline and its immutable release version.
