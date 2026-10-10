# Monthly statement export implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development for independent policy, DOM and UI work; root integrates workflow/downloads and reviews the completed change.

**Goal:** User-triggered export of the first current account's selected current-year month to an original bank PDF on Desktop.

**Architecture:** Reuse live DOM handlers in authenticated WebView2. Separate date/file policy, page adapter, workflow coordinator, download receiver and UI status. Capture expected child-window downloads with the parent's environment/profile.

**Tech Stack:** .NET 8 WPF, existing WebView2, JavaScript with Node/jsdom regression tests.

**Spec:** PROJECT/openspec/changes/implement-monthly-statement-export/design.md and specs/monthly-statement-export/spec.md.

## Global constraints

Current local year; selected month 1–12; complete calendar month; first account only; bank-generated PDF; desktop; no file overwrite; no credentials/transaction data in logs. Retain single installed entry, existing authentication and optional popup behavior. Snapshot handoff has no Git repository; use files, tests and manifest instead of commits/worktrees.

## Review focus

- Multiple accounts: only first selected, query retains same account in JS memory.
- Duplicate PDF menus: only download-all owner accepted, ambiguity fails closed.
- Stale async page/action: run IDs, one submission per phase, cancellation and bounded waits.
- Child download: original opener request preserved, matching source/origin only, no second GET.
- Partial/non-PDF or same-name output: no false success/no overwrite, only own temp removed.

## Task 1 — Month and PDF policy

Files: Browser/StatementExportMonth.cs, StatementPdfFile.cs and corresponding .NET tests.
Interfaces: StatementExportMonth.Create(DateTime today,int month), Year/Month/StartDate/EndDate (yyyyMMdd). StatementPdfFile.Create(string directory,StatementExportMonth month), TempPath, CompleteAsync(CancellationToken) returning final path, IDisposable cleanup.
- [x] Write failing tests for February/leap/year rollover/future month, PDF validation, collision and cancellation.
- [x] Implement policy and run filtered tests.

## Task 2 — Live DOM adapter

Files: Browser/CcbStatementExport.js and tests/browser/ccb-statement-export.test.cjs.
Interface: function(request) with operation inspect/open-accounts/select-first/query/download/dispose, runId, origin, startDate,endDate. Returns fixed status/stage codes only; no account values. Inspect returns home/accounts/query/results/unsupported/ambiguous. Action results opened/selected/queried/downloading/waiting/unsupported/ambiguous/failed; idempotent per run and document.
- [x] Write failing positive and rejection tests based on captured structure.
- [x] Implement first-account selection, date-setting with original submission and proven download-all ownership; run JS suite.

## Task 3 — Coordinator and download host

Files: Browser/StatementExportCoordinator.cs, WpfStatementExporter.cs, WpfStatementDownload.cs and coordinator tests.
Interfaces: coordinator consumes IStatementExportPage.ExecuteAsync(operation,range,runId,token) returning fixed status/stage and IStatementExportDownload.WaitAsync(token), IDisposable. Exporter owns WPF UI-thread lifetime and exposes ExportAsync(int month,CancellationToken) returning final file path, StateChanged/Message/IsBusy, IAsyncDisposable.
- [x] Test fake-adapter ordered transitions, rejection, duplicate/cancellation and timeout before implementing coordinator.
- [x] Create download receiver before final action, arm only for matching origin/known endpoint/source frame, initialize same-environment/profile child before NewWindow assignment, set only accepted PDF destination and watch terminal download state.
- [x] Verify PDF via policy and dispose all listeners/temp files/child on failure or cancellation.

## Task 4 — UI and integration

Files: Presentation/LauncherViewModel.cs, MainWindow.xaml, App.xaml.cs and view-model tests.
Interfaces: ConfigureExportCommand(Func<int,CancellationToken,Task>), UpdateExportState(bool busy,string? message); CanExportStatement/IsExportBusy/ExportStatusText/ExportStatementCommand.
- [x] Test initialized command availability, month forwarding, export busy blocks other actions and restores; bind UI.
- [x] Root wires exporter, cancels/drains on shutdown before browser disposal; remove temporary structure sampling hookup.

## Task 5 — Review and deliver

- [x] Run dotnet test PROJECT/UsbWebLauncher.sln -c Release --artifacts-path F:/USB-Web-Launcher-Build/page-login --verbosity quiet and all Node tests, review critical flows.
- [x] Publish outside workspace, verify artifact changes, back up then update original closed entry and manifest.
- [x] Mark deployment complete but keep real bank acceptance task pending until user confirms correct desktop PDF.

## Final decisions and evidence

Nested/hidden navigation and duplicate top/bottom all-download menus supported with ownership checks. Result identity requires account/date fields and original frame lineage. Only claimed child WebView downloads are accepted; cancellation drains pending child creation and retries owned temp cleanup. Inspect allows four transient unsupported samples; the fifth fails. .NET 978/978 and Node 166/166 passed; strict OpenSpec validation and external publish passed. Original App DLL/PDB deployed with hash verification; one executable/shortcut entry remains. See docs/monthly-statement-export-verification-20260930.md. Live automatic bank export remains unverified (OpenSpec task 2.3 is unchecked).
