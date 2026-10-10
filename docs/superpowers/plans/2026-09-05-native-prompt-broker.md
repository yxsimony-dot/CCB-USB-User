# Native Prompt Broker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在标准 PKCS#11 登录不能被 WebView2/CSP 复用时，由最小高权限代理精确识别厂商密码窗并提交一次本机 PIN，完成 TLS 后才保存凭据。

**Architecture:** 普通权限 WPF/WebView2 主程序继续负责 USB 周期、PIN 生命周期、证书精确选择和 TLS 结果；新增普通权限命名管道客户端与独立 High Integrity 代理。代理从管理员保护目录加载固定非秘密窗口策略，只接受当前用户、当前会话、单次尝试协议，精确验证 `CCBDetector.exe` 及窗口结构后使用 Unicode `SendInput`，不承载网页也不读取持久凭据。

**Tech Stack:** .NET 8、WPF、WebView2、Windows Named Pipes、Win32 User32/Kernel32/Advapi32/WinTrust、Task Scheduler、xUnit。

**Spec:** `openspec/changes/add-client-certificate-auto-selection/design.md` 与 `openspec/changes/add-client-certificate-auto-selection/specs/client-certificate-token-authentication/spec.md`

## Global Constraints

- 主应用及 WebView2 必须保持 Medium Integrity；只有独立代理以 High Integrity 运行。
- PIN 不得进入命令行、剪贴板、JSON、普通文件、日志、异常或状态文本。
- 每个 USB 插入周期最多执行一次令牌登录和一次厂商窗口提交，不自动试错。
- 代理只接受当前用户命名管道，并以管理员保护目录中的固定策略匹配唯一目标窗口。
- 任一身份、窗口、焦点、协议或超时检查失败都必须默认拒绝并清零秘密缓冲区。
- 默认自动测试不得访问真实 USB 私钥、提交 PIN、发送键盘输入或访问外部站点。

---

### Task 1: Continuation configuration and validation

**Files:**
- Modify: `src/UsbWebLauncher.Core/Configuration/LauncherProfile.cs`
- Modify: `src/UsbWebLauncher.Core/Configuration/LauncherProfileValidator.cs`
- Modify: `src/UsbWebLauncher.App/launcher-profile.json`
- Test: `tests/UsbWebLauncher.Tests/Configuration/LauncherProfileValidatorTests.cs`
- Test: `tests/UsbWebLauncher.Tests/Configuration/LauncherProfileJsonTests.cs`

**Interfaces:**
- Produces: `ClientCertificateNativePromptOptions` and inclusion in `ClientCertificateAuthenticationOptions.GetNormalizedHash()`.
- Consumes: existing profile JSON parser and validation error model.

- [ ] **Step 1: Write failing configuration tests**

```csharp
[Fact]
public void Validate_RejectsNativePromptPolicyWithRelativeProcessPath()
{
    var profile = ValidProfile() with
    {
        ClientCertificateAuthentication = ValidAuthentication() with
        {
            NativePromptContinuation = ValidNativePrompt() with { ProcessPath = @"CCBDetector.exe" }
        }
    };

    Assert.Contains(LauncherProfileValidator.Validate(profile).Errors,
        error => error.Field.EndsWith("processPath", StringComparison.Ordinal));
}

[Fact]
public void NormalizedHash_ChangesWhenNativePromptBinaryHashChanges()
{
    var first = ValidAuthentication() with { NativePromptContinuation = ValidNativePrompt() };
    var second = first with
    {
        NativePromptContinuation = first.NativePromptContinuation! with
        {
            ProcessSha256 = new string('B', 64)
        }
    };

    Assert.NotEqual(first.GetNormalizedHash(), second.GetNormalizedHash());
}
```

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/UsbWebLauncher.Tests/UsbWebLauncher.Tests.csproj -c Release --filter "FullyQualifiedName~LauncherProfile"`

Expected: compilation fails because `NativePromptContinuation` and `ClientCertificateNativePromptOptions` do not exist.

- [ ] **Step 3: Implement minimal configuration model and validation**

```csharp
public sealed record ClientCertificateNativePromptOptions(
    bool Enabled,
    string BrokerPipeName,
    string PolicyId,
    string ProcessPath,
    string ProcessSha256,
    string PublisherCertificateThumbprint,
    string WindowTitle,
    string WindowClass,
    IReadOnlyList<string> RequiredChildWindowClasses,
    int TimeoutMilliseconds);
```

Validate absolute local `.exe` path, 64-character SHA-256, 40-character certificate thumbprint, non-empty policy/pipe/title/class/child classes, and timeout from 1,000 through 30,000 milliseconds. Add every field to the normalized authentication hash. Configure the current policy with the observed process path, hash, signer thumbprint, title, class, child class chain, and a 10-second timeout.

- [ ] **Step 4: Verify GREEN**

Run: `dotnet test tests/UsbWebLauncher.Tests/UsbWebLauncher.Tests.csproj -c Release --filter "FullyQualifiedName~LauncherProfile"`

Expected: all selected tests pass.

### Task 2: Core continuation lifecycle

**Files:**
- Modify: `src/UsbWebLauncher.Core/Authentication/AuthenticationContracts.cs`
- Modify: `src/UsbWebLauncher.Core/Authentication/AuthenticationCoordinator.cs`
- Modify: `src/UsbWebLauncher.Core/Authentication/ClientCertificateAuthenticationFlowRouter.cs`
- Modify: `src/UsbWebLauncher.Core/Authentication/ClientCertificatePinSetupService.cs`
- Test: `tests/UsbWebLauncher.Tests/Authentication/AuthenticationCoordinatorTests.cs`
- Test: `tests/UsbWebLauncher.Tests/Authentication/ClientCertificateAuthenticationFlowRouterTests.cs`
- Test: `tests/UsbWebLauncher.Tests/Authentication/ClientCertificatePinSetupServiceTests.cs`

**Interfaces:**
- Produces: `IClientCertificateNativePromptContinuation.ArmAsync(...)`, `IClientCertificateNativePromptAttempt`, and classified arm/completion results.
- Consumes: `AuthenticationAttempt`, `ClientCertificatePin`, exact certificate-selection acknowledgement, and TLS terminal callbacks.

- [ ] **Step 1: Write failing lifecycle tests**

```csharp
[Fact]
public async Task CertificateSelection_ArmsContinuationOnceBeforeAuthorization()
{
    var continuation = new RecordingNativePromptContinuation();
    var coordinator = CreateCoordinator(continuation: continuation, savedPin: "random-test-value");
    await coordinator.HandleDevicePresenceAsync(true);
    await coordinator.ReportNavigationStartedAsync(41);
    var selected = await coordinator.HandleAsync(MatchingRequest(browserOperationId: 41));

    var authorization = await coordinator.ReportCertificateSelectedAsync(selected.Attempt!);

    Assert.NotNull(authorization);
    Assert.Equal(1, continuation.ArmCount);
}

[Fact]
public async Task RepeatedCertificateAcknowledgement_DoesNotSubmitPinTwice()
{
    var continuation = new RecordingNativePromptContinuation();
    var coordinator = await CreateAuthenticatingCoordinatorAsync(continuation);

    await coordinator.ReportCertificateSelectedAsync(coordinator.CurrentState.Attempt!);
    await coordinator.ReportCertificateSelectedAsync(coordinator.CurrentState.Attempt!);

    Assert.Equal(1, continuation.ArmCount);
}
```

Add separate tests proving arm failure returns no authorization, terminal TLS/cancel/device removal disposes and clears the attempt, setup passes the in-memory PIN without persisting it early, and continuation failure prevents setup success.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/UsbWebLauncher.Tests/UsbWebLauncher.Tests.csproj -c Release --filter "FullyQualifiedName~AuthenticationCoordinator|FullyQualifiedName~FlowRouter|FullyQualifiedName~PinSetupService"`

Expected: compilation fails because the continuation contracts and constructor dependencies do not exist.

- [ ] **Step 3: Implement minimal lifecycle**

```csharp
public interface IClientCertificateNativePromptContinuation
{
    ValueTask<ClientCertificateNativePromptArmResult> ArmAsync(
        ClientCertificateAuthenticationOptions authentication,
        AuthenticationAttempt attempt,
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default);
}

public interface IClientCertificateNativePromptAttempt : IAsyncDisposable
{
    Task<ClientCertificateNativePromptResult> Completion { get; }
}
```

Retain the PIN only from successful token unlock until certificate selection. Arm at most once before returning the selection authorization. The setup router receives the still-unpersisted PIN through `VerifyAsync`; both normal and setup paths dispose the continuation attempt and PIN on TLS success/failure, timeout, cancellation, device removal, or runtime disposal.

- [ ] **Step 4: Verify GREEN**

Run the Task 2 filtered command and expect all selected tests to pass.

### Task 3: Framed named-pipe protocol and client

**Files:**
- Create: `src/UsbWebLauncher.Windows/Authentication/NativePrompt/NativePromptProtocol.cs`
- Create: `src/UsbWebLauncher.Windows/Authentication/NativePrompt/WindowsNativePromptContinuationClient.cs`
- Create: `src/UsbWebLauncher.Windows/Authentication/NativePrompt/WindowsNamedPipePeerInspector.cs`
- Test: `tests/UsbWebLauncher.Tests/Windows/Authentication/NativePromptProtocolTests.cs`
- Test: `tests/UsbWebLauncher.Tests/Windows/Authentication/WindowsNativePromptContinuationClientTests.cs`

**Interfaces:**
- Produces: bounded binary request/response protocol and Core continuation implementation.
- Consumes: broker pipe name, policy ID/hash, attempt ID, timeout, and short-lived PIN buffer.

- [ ] **Step 1: Write failing protocol/client tests**

```csharp
[Fact]
public void Decode_RejectsFrameLargerThanProtocolMaximum()
{
    var header = NativePromptTestFrames.Header(payloadLength: NativePromptProtocol.MaxPayloadBytes + 1);
    Assert.Throws<InvalidDataException>(() => NativePromptProtocol.Decode(header));
}

[Fact]
public async Task ArmAsync_ClearsTransportPinAfterBrokerAcknowledges()
{
    var transport = new RecordingPromptTransport(ClientCertificateNativePromptArmStatus.Armed);
    var client = new WindowsNativePromptContinuationClient(transport);
    using var pin = ClientCertificatePin.CopyOf("random-test-value".AsSpan());

    var result = await client.ArmAsync(Authentication(), Attempt(), pin);

    Assert.Equal(ClientCertificateNativePromptArmStatus.Armed, result.Status);
    Assert.All(transport.CapturedPinBuffer!, value => Assert.Equal(0, value));
}
```

Cover wrong magic/version, negative/oversized lengths, truncated reads, unexpected response, server PID/path/integrity mismatch, timeout/cancel/disconnect, and attempt-ID replay response. Test doubles must capture buffers by reference so clearing is observable.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/UsbWebLauncher.Tests/UsbWebLauncher.Tests.csproj -c Release --filter "FullyQualifiedName~NativePromptProtocol|FullyQualifiedName~WindowsNativePromptContinuationClient"`

Expected: compilation fails because the protocol and client do not exist.

- [ ] **Step 3: Implement protocol and client**

Use a fixed magic, protocol version 1, maximum 16 KiB frame, maximum 128 PIN characters, exact reads, explicit status enum, and no object/string representation containing payload bytes. Create the pipe with asynchronous mode, verify the connected server PID resolves to the configured administrator-protected broker path and High Integrity, send the PIN only after peer verification, and clear all byte/char buffers in `finally`.

- [ ] **Step 4: Verify GREEN**

Run the Task 3 filtered command and expect all selected tests to pass.

### Task 4: Exact native-window matcher and input executor

**Files:**
- Create: `src/UsbWebLauncher.Windows/Authentication/NativePrompt/NativePromptWindowSnapshot.cs`
- Create: `src/UsbWebLauncher.Windows/Authentication/NativePrompt/NativePromptWindowMatcher.cs`
- Create: `src/UsbWebLauncher.Windows/Authentication/NativePrompt/WindowsNativePromptApi.cs`
- Create: `src/UsbWebLauncher.Windows/Authentication/NativePrompt/WindowsNativePromptExecutor.cs`
- Test: `tests/UsbWebLauncher.Tests/Windows/Authentication/NativePromptWindowMatcherTests.cs`
- Test: `tests/UsbWebLauncher.Tests/Windows/Authentication/WindowsNativePromptExecutorTests.cs`

**Interfaces:**
- Produces: pure snapshot matcher and high-integrity executor used only by the broker server.
- Consumes: installed immutable policy and secret buffer received over the pipe.

- [ ] **Step 1: Write failing matcher/executor tests**

```csharp
[Theory]
[InlineData(false, true, true, true, true)]
[InlineData(true, false, true, true, true)]
[InlineData(true, true, false, true, true)]
[InlineData(true, true, true, false, true)]
[InlineData(true, true, true, true, false)]
public void Match_RejectsAnyIdentityDifference(
    bool path, bool hash, bool session, bool integrity, bool window)
{
    var candidate = Candidate(path, hash, session, integrity, window);
    Assert.False(NativePromptWindowMatcher.IsMatch(Policy(), candidate));
}

[Fact]
public async Task ExecuteAsync_FocusChangesBeforeFirstCharacter_SendsNoInput()
{
    var native = new RecordingNativePromptApi { ForegroundChangesAfterActivation = true };
    var executor = new WindowsNativePromptExecutor(native);
    using var pin = ClearableSecretBuffer.FromCharacters("random-test-value".AsSpan());

    var result = await executor.ExecuteAsync(Policy(), pin, CancellationToken.None);

    Assert.Equal(ClientCertificateNativePromptStatus.FocusLost, result.Status);
    Assert.Empty(native.InputBatches);
}
```

Cover zero/multiple candidates, invalid Authenticode status, hash mismatch, wrong user/session/integrity/title/class/child chain, invisible/disabled window, foreground mismatch, partial `SendInput`, one Enter only, cancellation, and buffer clearing.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/UsbWebLauncher.Tests/UsbWebLauncher.Tests.csproj -c Release --filter "FullyQualifiedName~NativePromptWindowMatcher|FullyQualifiedName~WindowsNativePromptExecutor"`

Expected: compilation fails because matcher/executor types do not exist.

- [ ] **Step 3: Implement matcher and executor**

Enumerate visible enabled top-level windows in the current interactive session; open candidate processes with query-only rights; verify process path, SHA-256, WinVerifyTrust signer thumbprint, owner SID, session ID and High Integrity. Require one exact candidate and the configured descendant-class sequence. Activate it, wait conditionally for foreground equality, rebuild and compare the snapshot immediately before input, then send Unicode key-down/key-up pairs plus one Enter in a single bounded batch. Treat any partial send as failure and never retry.

- [ ] **Step 4: Verify GREEN**

Run the Task 4 filtered command and expect all selected tests to pass.

### Task 5: Elevated broker process and protected installation

**Files:**
- Create: `src/UsbWebLauncher.NativePromptBroker/UsbWebLauncher.NativePromptBroker.csproj`
- Create: `src/UsbWebLauncher.NativePromptBroker/Program.cs`
- Create: `src/UsbWebLauncher.Windows/Authentication/NativePrompt/WindowsNativePromptBrokerServer.cs`
- Create: `src/UsbWebLauncher.Windows/Authentication/NativePrompt/WindowsNativePromptBrokerInstaller.cs`
- Modify: `UsbWebLauncher.sln`
- Test: `tests/UsbWebLauncher.Tests/Windows/Authentication/WindowsNativePromptBrokerServerTests.cs`
- Test: `tests/UsbWebLauncher.Tests/Windows/Authentication/WindowsNativePromptBrokerInstallerTests.cs`

**Interfaces:**
- Produces: `--server`, `--install <policy-file>`, `--uninstall`, health request, and current-user highest-privilege scheduled-task lifecycle.
- Consumes: protocol, matcher/executor, and non-secret policy JSON.

- [ ] **Step 1: Write failing broker/install tests**

```csharp
[Fact]
public async Task Server_RejectsReplayedAttemptWithoutCallingExecutorTwice()
{
    var executor = new RecordingPromptExecutor();
    var server = CreateServer(executor);
    var request = ValidRequest(attemptId: Guid.NewGuid());

    await server.ProcessAsync(request);
    var replay = await server.ProcessAsync(request);

    Assert.Equal(ClientCertificateNativePromptArmStatus.Replayed, replay.Status);
    Assert.Equal(1, executor.CallCount);
}

[Fact]
public async Task Installer_UsesProtectedProgramFilesPathAndHighestInteractiveTask()
{
    var os = new RecordingBrokerInstallationApi();
    await new WindowsNativePromptBrokerInstaller(os).InstallAsync(Package(), CancellationToken.None);
    Assert.True(os.TaskDefinition.RunOnlyForInteractiveUser);
    Assert.Equal(BrokerRunLevel.Highest, os.TaskDefinition.RunLevel);
}
```

Cover malformed policy, policy hash mismatch, wrong client SID/session, simultaneous clients, server shutdown, install copy failure rollback, task registration/start failure, health/version mismatch, and uninstall cleanup.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/UsbWebLauncher.Tests/UsbWebLauncher.Tests.csproj -c Release --filter "FullyQualifiedName~NativePromptBroker"`

Expected: compilation fails because broker server and installer do not exist.

- [ ] **Step 3: Implement broker and installer**

The single-file broker executable branches only on `--server`, `--install <absolute-policy-path>`, and `--uninstall`. Installation requires High Integrity, copies the broker and policy to `%ProgramFiles%\UsbWebLauncher\NativePromptBroker`, applies administrator/system write ACLs, registers `UsbWebLauncher Native Prompt Broker` as an interactive-token highest-run-level task, starts it, and verifies protocol health. Uninstall stops/deletes the task and removes only the exact installation directory after validating its resolved path.

- [ ] **Step 4: Verify GREEN**

Run the Task 5 filtered command and expect all selected tests to pass.

### Task 6: Application composition and local setup UX

**Files:**
- Modify: `src/UsbWebLauncher.App/Composition/WindowsClientCertificateAuthenticationRuntime.cs`
- Modify: `src/UsbWebLauncher.App/Composition/LauncherApplicationComposer.cs`
- Modify: `src/UsbWebLauncher.App/Presentation/LauncherViewModel.cs`
- Modify: `src/UsbWebLauncher.App/Presentation/LocalPinCommands.cs`
- Modify: `src/UsbWebLauncher.App/MainWindow.xaml`
- Modify: `src/UsbWebLauncher.App/MainWindow.xaml.cs`
- Test: `tests/UsbWebLauncher.Tests/Composition/LauncherApplicationComposerTests.cs`
- Test: `tests/UsbWebLauncher.Tests/Presentation/LauncherViewModelTests.cs`
- Test: `tests/UsbWebLauncher.Tests/Presentation/LocalPinCommandsTests.cs`
- Test: `tests/UsbWebLauncher.Tests/Integration/ClientCertificateAuthenticationEndToEndTests.cs`

**Interfaces:**
- Produces: shared continuation client in normal/setup paths and explicit install/uninstall/health commands.
- Consumes: Windows broker installer and existing state presenter.

- [ ] **Step 1: Write failing composition and UX tests**

```csharp
[Fact]
public async Task ValidateAndSave_WhenBrokerMissing_RequestsInstallationBeforePinDialog()
{
    var prerequisites = new RecordingPromptBrokerPrerequisites { Installed = false };
    var commands = CreateCommands(prerequisites);

    await commands.ValidateAndSaveAsync();

    Assert.Equal(new[] { "Install", "Health", "ShowPinDialog", "Validate" }, prerequisites.Events);
}

[Fact]
public async Task EndToEnd_PromptMismatch_DoesNotPersistSetupPin()
{
    var fixture = EndToEndFixture.WithPromptResult(ClientCertificateNativePromptStatus.TargetMismatch);
    var result = await fixture.ValidateAndSaveAsync("random-test-value");
    Assert.False(result.IsSuccess);
    Assert.Equal(0, fixture.PinStore.SaveCount);
}
```

Cover user-cancelled UAC, broker version mismatch, install retry, normal saved-PIN path, setup path, TLS success persistence, TLS failure rollback, and user-visible non-secret status.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/UsbWebLauncher.Tests/UsbWebLauncher.Tests.csproj -c Release --filter "FullyQualifiedName~LauncherApplicationComposer|FullyQualifiedName~LauncherViewModel|FullyQualifiedName~LocalPinCommands|FullyQualifiedName~ClientCertificateAuthenticationEndToEnd"`

Expected: tests fail because prerequisite commands and continuation composition are missing.

- [ ] **Step 3: Implement composition and UX**

Create one `WindowsNativePromptContinuationClient` per authentication runtime and share it between coordinator and setup router. Add an “安装本机认证组件” action visible only when configured continuation is unhealthy. Launch bundled broker with `ProcessStartInfo.UseShellExecute = true` and `Verb = "runas"`; arguments contain only `--install` and an absolute non-secret policy path. After install, require a successful health/version check before opening the local password dialog.

- [ ] **Step 4: Verify GREEN**

Run the Task 6 filtered command and expect all selected tests to pass.

### Task 7: Security verification, documentation, and release

**Files:**
- Modify: `README.md`
- Modify: `openspec/changes/add-client-certificate-auto-selection/tasks.md`
- Create: `src/UsbWebLauncher.NativePromptBroker/native-prompt-policy.json`
- Modify: publish scripts or project publish properties already used by the repository

**Interfaces:**
- Produces: self-contained `win-x64` application package plus single-file broker and non-secret policy.
- Consumes: all previous tasks.

- [ ] **Step 1: Run the full automated suite**

Run: `dotnet test UsbWebLauncher.sln -c Release --no-restore`

Expected: all tests pass with no warnings or skipped default tests accessing hardware.

- [ ] **Step 2: Run static secret and unsafe-input scans**

Run: `rg -n -i "password\s*[:=]|pin\s*[:=]|clipboard|SetClipboardData|SendKeys|mouse_event" src tests artifacts openspec`

Expected: no real secret, clipboard transport, coordinate click, `SendKeys`, or unguarded input path; expected type/property names are reviewed manually and contain no values.

- [ ] **Step 3: Run explicit no-secret hardware preflight**

Run: `dotnet test tests/UsbWebLauncher.Tests/UsbWebLauncher.Tests.csproj -c Release --filter "Hardware" -- RunConfiguration.EnvironmentVariables.USB_WEB_LAUNCHER_RUN_HARDWARE_TESTS=true`

Expected: USB, PKCS#11, CSP container, broker process identity, signature/hash, integrity and current prompt structure probes pass without submitting PIN or input.

- [ ] **Step 4: Publish into a new project-local directory**

Run: `dotnet publish src/UsbWebLauncher.App/UsbWebLauncher.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64-native-prompt`

Run: `dotnet publish src/UsbWebLauncher.NativePromptBroker/UsbWebLauncher.NativePromptBroker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish/win-x64-native-prompt/broker`

Expected: both outputs remain under the repository `artifacts` directory; OpenSpec npm cache remains under `D:\CodexSkills`.

- [ ] **Step 5: Validate OpenSpec and update only completed task checkboxes**

Run with D-drive npm cache: `npx @fission-ai/openspec validate add-client-certificate-auto-selection --strict`

Expected: validation passes; real PIN/TLS tasks remain unchecked until the user completes local UAC and PIN entry.

- [ ] **Step 6: Commit implementation**

Stage only the OpenSpec, source, test, documentation, and plan files changed for this feature. Do not stage the pre-existing `.agents/` directory. Commit message: `feat: continue native certificate prompt securely`.

