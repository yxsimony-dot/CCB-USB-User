using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication;
using UsbWebLauncher.Windows.Authentication.NativePrompt;
using System.Text.Json;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsNativePromptBrokerServerTests
{
    [Fact]
    public async Task ProductionPipeFactory_UsesCurrentUserOnlyDaclAndMediumIntegrityLabel()
    {
        var currentUser = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The test requires a Windows user SID.");
        var pipeName = $"UsbWebLauncher.Tests.Security.{Guid.NewGuid():N}";

        await using var pipe = WindowsNativePromptBrokerPipeFactory.Create(
            pipeName,
            currentUser);

        var security = pipe.GetAccessControl();
        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));
        var rule = Assert.Single(rules.Cast<PipeAccessRule>());
        Assert.Equal(currentUser, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(
            PipeAccessRights.FullControl,
            rule.PipeAccessRights & PipeAccessRights.FullControl);
        var labelSddl = NativePipeSecurity.ReadMandatoryLabelSddl(pipe.SafePipeHandle);
        Assert.Contains("S:(ML;;NW;;;ME)", labelSddl, StringComparison.Ordinal);
    }

    private static class NativePipeSecurity
    {
        private const int SeKernelObject = 6;
        private const uint SecurityDescriptorRevision = 1;
        private const uint LabelSecurityInformation = 0x00000010;

        public static string ReadMandatoryLabelSddl(
            Microsoft.Win32.SafeHandles.SafePipeHandle pipeHandle)
        {
            var result = GetSecurityInfo(
                pipeHandle,
                SeKernelObject,
                LabelSecurityInformation,
                out _,
                out _,
                out _,
                out _,
                out var securityDescriptor);
            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"GetSecurityInfo failed with error {result}.");
            }

            try
            {
                if (!ConvertSecurityDescriptorToStringSecurityDescriptor(
                        securityDescriptor,
                        SecurityDescriptorRevision,
                        LabelSecurityInformation,
                        out var sddl,
                        out _))
                {
                    throw new InvalidOperationException(
                        $"Converting the pipe label failed with error {Marshal.GetLastWin32Error()}.");
                }

                try
                {
                    return Marshal.PtrToStringUni(sddl) ?? string.Empty;
                }
                finally
                {
                    _ = LocalFree(sddl);
                }
            }
            finally
            {
                _ = LocalFree(securityDescriptor);
            }
        }

        [DllImport("Advapi32.dll", SetLastError = true)]
        private static extern uint GetSecurityInfo(
            Microsoft.Win32.SafeHandles.SafePipeHandle handle,
            int objectType,
            uint securityInformation,
            out nint owner,
            out nint group,
            out nint dacl,
            out nint sacl,
            out nint securityDescriptor);

        [DllImport(
            "Advapi32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(
            nint securityDescriptor,
            uint requestedStringSdRevision,
            uint securityInformation,
            out nint stringSecurityDescriptor,
            out uint stringSecurityDescriptorLength);

        [DllImport("Kernel32.dll")]
        private static extern nint LocalFree(nint memory);
    }

    [Fact]
    public async Task Host_RejectsUnboundClientBeforeReadingTheHealthFrame()
    {
        var pipeName = $"UsbWebLauncher.Tests.{Guid.NewGuid():N}";
        await using var server = CreateServer(new RecordingExecutor());
        var inspector = new RecordingClientInspector(
            ValidIdentity() with { ProcessSha256 = OtherHash });
        var protocol = new RecordingHostProtocol();
        var host = new WindowsNativePromptBrokerHost(server, pipeName, inspector, protocol);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hostTask = host.RunAsync(timeout.Token);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await client.ConnectAsync(timeout.Token);
        await inspector.Inspected.Task.WaitAsync(timeout.Token);
        var probe = new byte[1];
        var read = await client.ReadAsync(probe, timeout.Token);

        Assert.Equal(0, read);
        Assert.Equal(0, protocol.ReadHealthCount);
        timeout.Cancel();
        await hostTask;
    }

    [Fact]
    public async Task Host_AcceptsAnotherClientAfterRejectingTheFirstClient()
    {
        var pipeName = $"UsbWebLauncher.Tests.Sequential.{Guid.NewGuid():N}";
        await using var server = CreateServer(new RecordingExecutor());
        var inspector = new RecordingClientInspector(
            ValidIdentity() with { ProcessSha256 = OtherHash });
        var protocol = new RecordingHostProtocol();
        var host = new WindowsNativePromptBrokerHost(server, pipeName, inspector, protocol);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hostTask = host.RunAsync(timeout.Token);

        await using (var first = new NamedPipeClientStream(
                         ".",
                         pipeName,
                         PipeDirection.InOut,
                         PipeOptions.Asynchronous))
        {
            await first.ConnectAsync(timeout.Token);
            await inspector.Inspected.Task.WaitAsync(timeout.Token);
            Assert.Equal(0, await first.ReadAsync(new byte[1], timeout.Token));
        }

        await using (var second = new NamedPipeClientStream(
                         ".",
                         pipeName,
                         PipeDirection.InOut,
                         PipeOptions.Asynchronous))
        {
            await second.ConnectAsync(timeout.Token);
            await inspector.InspectedTwice.Task.WaitAsync(timeout.Token);
            Assert.Equal(0, await second.ReadAsync(new byte[1], timeout.Token));
        }

        Assert.False(hostTask.IsCompleted);
        Assert.Equal(0, protocol.ReadHealthCount);
        timeout.Cancel();
        await hostTask;
    }

    [Fact]
    public async Task Host_ConcurrentTrustedHealthChecksDoNotTerminateTheBroker()
    {
        var pipeName = $"UsbWebLauncher.Tests.ConcurrentHealth.{Guid.NewGuid():N}";
        await using var server = CreateServer(new RecordingExecutor());
        var inspector = new RecordingClientInspector(ValidIdentity());
        var host = new WindowsNativePromptBrokerHost(server, pipeName, inspector);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hostTask = host.RunAsync(timeout.Token);

        async Task<NativePromptBrokerHealthResult> CheckHealthAsync()
        {
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            await NativePromptProtocol.WriteHealthRequestAsync(client, timeout.Token);
            return await NativePromptProtocol.ReadHealthResponseAsync(client, timeout.Token);
        }

        var results = await Task.WhenAll(CheckHealthAsync(), CheckHealthAsync());
        await Task.Delay(100, timeout.Token);

        Assert.All(results, result => Assert.True(result.IsHealthy));
        Assert.False(hostTask.IsCompleted);
        timeout.Cancel();
        await hostTask;
    }

    [Fact]
    public async Task Host_AuthenticatedHealthFinalizesPendingBrokerUpdateBeforeReadingArmRequest()
    {
        var events = new List<string>();
        var pipeName = $"UsbWebLauncher.Tests.Finalize.{Guid.NewGuid():N}";
        await using var server = CreateServer(new RecordingExecutor());
        var inspector = new RecordingClientInspector(ValidIdentity(), events);
        var protocol = new RecordingHostProtocol(events);
        var finalizer = new RecordingUpdateFinalizer(events);
        var host = new WindowsNativePromptBrokerHost(
            server,
            pipeName,
            inspector,
            protocol,
            finalizer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hostTask = host.RunAsync(timeout.Token);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await client.ConnectAsync(timeout.Token);
        await finalizer.Finalized.Task.WaitAsync(timeout.Token);
        var probe = new byte[1];
        _ = await client.ReadAsync(probe, timeout.Token);

        Assert.Equal(
            ["Inspect", "ReadHealth", "WriteHealth", "Finalize", "ReadArm"],
            events);
        timeout.Cancel();
        await hostTask;
    }

    [Fact]
    public async Task ProductionClientInspectorReportsConnectedProcessAndAllApplicationCodeHashes()
    {
        var pipeName = $"UsbWebLauncher.Tests.Inspector.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = server.WaitForConnectionAsync(timeout.Token);

        await client.ConnectAsync(timeout.Token);
        await accepted;
        var identity = new WindowsNativePromptBrokerClientInspector().Inspect(server);

        Assert.NotNull(identity);
        var processPath = Path.GetFullPath(Environment.ProcessPath!);
        Assert.Equal(processPath, identity!.ProcessPath, ignoreCase: true);
        Assert.Equal(HashFile(processPath), identity.ProcessSha256);
        var directory = Path.GetDirectoryName(processPath)!;
        Assert.Equal(
            HashFile(Path.Combine(directory, NativePromptBrokerTrustedClient.ApplicationAssemblyFileName)),
            identity.ApplicationAssemblySha256);
        Assert.Equal(
            HashFile(Path.Combine(directory, NativePromptBrokerTrustedClient.CoreAssemblyFileName)),
            identity.CoreAssemblySha256);
        Assert.Equal(
            HashFile(Path.Combine(directory, NativePromptBrokerTrustedClient.WindowsAssemblyFileName)),
            identity.WindowsAssemblySha256);
    }

    [Fact]
    public void PolicyFile_LoadsStrictCompleteNonSecretPolicy()
    {
        var path = WritePolicyFile(Policy());
        try
        {
            var policy = NativePromptBrokerPolicyFile.Load(path);

            Assert.True(policy.IsPackageValid);
            Assert.False(policy.IsValid);
            Assert.Equal("policy-v1", policy.Prompt.PolicyId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PolicyFile_BindsAValidatedTrustedClientForInstalledRuntime()
    {
        var packageJson = JsonSerializer.SerializeToUtf8Bytes(new NativePromptBrokerPolicyDocument(
            NativePromptProtocol.ProtocolVersion,
            ValidHash,
            Policy()));
        var trustedClient = new NativePromptBrokerTrustedClient(
            ClientProcessPath,
            ClientProcessHash,
            ClientApplicationHash,
            ClientCoreHash,
            ClientWindowsHash);

        var installedJson = NativePromptBrokerPolicyFile.BindTrustedClient(
            packageJson,
            trustedClient);
        var installed = NativePromptBrokerPolicyFile.Parse(installedJson);

        Assert.True(installed.IsValid);
        Assert.Equal(trustedClient, installed.TrustedClient);
        Assert.Throws<InvalidDataException>(() =>
            NativePromptBrokerPolicyFile.BindTrustedClient(installedJson, trustedClient));
    }

    [Fact]
    public void PolicyFile_LoadInstalledRejectsStaticPackageAndAcceptsBoundPolicy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"native-prompt-policy-{Guid.NewGuid():N}.json");
        var packageJson = JsonSerializer.SerializeToUtf8Bytes(new NativePromptBrokerPolicyDocument(
            NativePromptProtocol.ProtocolVersion,
            ValidHash,
            Policy()));
        var trustedClient = new NativePromptBrokerTrustedClient(
            ClientProcessPath,
            ClientProcessHash,
            ClientApplicationHash,
            ClientCoreHash,
            ClientWindowsHash);
        try
        {
            File.WriteAllBytes(path, packageJson);
            Assert.Throws<InvalidDataException>(() => NativePromptBrokerPolicyFile.LoadInstalled(path));

            File.WriteAllBytes(
                path,
                NativePromptBrokerPolicyFile.BindTrustedClient(packageJson, trustedClient));
            var installed = NativePromptBrokerPolicyFile.LoadInstalled(path);

            Assert.True(installed.IsValid);
            Assert.Equal(trustedClient, installed.TrustedClient);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PolicyFile_RejectsForbiddenSecretField()
    {
        var path = WritePolicyFile(Policy(), includeSecretField: true);
        try
        {
            Assert.Throws<InvalidDataException>(() => NativePromptBrokerPolicyFile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PolicyFile_RejectsMalformedPromptFingerprintBeforeBrokerCanBeHealthy()
    {
        var path = WritePolicyFile(Policy() with { ProcessSha256 = "not-a-hash" });
        try
        {
            Assert.Throws<InvalidDataException>(() => NativePromptBrokerPolicyFile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PolicyFile_AcceptsStrictTrustedClientDescendantPolicyWithoutVersionedPathHash()
    {
        var prompt = NativePromptWindowMatcherTests.DescendantPolicy(1000) with
        {
            BrokerPipeName = WindowsNativePromptBrokerInstaller.BrokerPipeName
        };
        var path = WritePolicyFile(prompt);
        try
        {
            var policy = NativePromptBrokerPolicyFile.Load(path);

            Assert.True(policy.IsPackageValid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PolicyFile_AcceptsStrictTrustedClientTopLevelPolicyForUnsignedBoundClient()
    {
        var prompt = NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy(1000) with
        {
            BrokerPipeName = WindowsNativePromptBrokerInstaller.BrokerPipeName
        };
        var path = WritePolicyFile(prompt);
        try
        {
            var policy = NativePromptBrokerPolicyFile.Load(path);

            Assert.True(policy.IsPackageValid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{\"protocolVersion\":1,\"PolicyHash\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"Prompt\":{}}")]
    [InlineData("{\"ProtocolVersion\":1,\"ProtocolVersion\":1,\"PolicyHash\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"Prompt\":{}}")]
    public void PolicyFile_RejectsCaseVariantOrDuplicateProperties(string json)
    {
        Assert.Throws<InvalidDataException>(() =>
            NativePromptBrokerPolicyFile.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task HealthProtocol_RoundTripsReadyProtocolVersionWithoutSecretPayload()
    {
        await using var stream = new MemoryStream();
        await NativePromptProtocol.WriteHealthRequestAsync(stream, CancellationToken.None);
        stream.Position = 0;
        await NativePromptProtocol.ReadHealthRequestAsync(stream, CancellationToken.None);
        Assert.Equal(NativePromptProtocol.HeaderBytes, stream.Length);

        stream.SetLength(0);
        await NativePromptProtocol.WriteHealthResponseAsync(
            stream,
            new NativePromptBrokerHealthResult(true, NativePromptProtocol.ProtocolVersion),
            CancellationToken.None);
        stream.Position = 0;

        var health = await NativePromptProtocol.ReadHealthResponseAsync(stream, CancellationToken.None);

        Assert.True(health.IsHealthy);
        Assert.Equal(NativePromptProtocol.ProtocolVersion, health.ProtocolVersion);
    }

    [Fact]
    public async Task ArmAsync_ValidRequestArmsBeforeExecutingAndClearsTransportPin()
    {
        var executor = new RecordingExecutor();
        var server = CreateServer(executor);
        var pin = RandomPin();
        using var request = Request(pin: pin);

        await using var result = await server.ArmAsync(ValidIdentity(), request);

        Assert.Equal(ClientCertificateNativePromptArmStatus.Armed, result.Response.Status);
        Assert.NotNull(result.Attempt);
        Assert.Equal(0, executor.CallCount);
        Assert.All(pin, value => Assert.Equal('\0', value));

        var completion = await result.Attempt!.ExecuteAsync();
        Assert.Equal(ClientCertificateNativePromptStatus.Success, completion.Status);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(2468, executor.ObservedTrustedClientProcessId);
    }

    [Fact]
    public async Task ArmAsync_ReplayedFullNonceDoesNotExecuteTwice()
    {
        var executor = new RecordingExecutor();
        var server = CreateServer(executor);
        var attempt = new NativePromptAttemptId(7, 1, 3);
        await using var first = await server.ArmAsync(ValidIdentity(), Request(attempt));
        _ = await first.Attempt!.ExecuteAsync();

        await using var replay = await server.ArmAsync(
            ValidIdentity(),
            Request(attempt));

        Assert.Equal(ClientCertificateNativePromptArmStatus.Replayed, replay.Response.Status);
        Assert.Null(replay.Attempt);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task ArmAsync_SameHighHalfWithDifferentLowHalfIsANewAttempt()
    {
        var executor = new RecordingExecutor();
        var server = CreateServer(executor);
        await using var first = await server.ArmAsync(
            ValidIdentity(),
            Request(new NativePromptAttemptId(7, 1, 3)));
        _ = await first.Attempt!.ExecuteAsync();

        await using var second = await server.ArmAsync(
            ValidIdentity(),
            Request(new NativePromptAttemptId(7, 2, 3)));

        Assert.Equal(ClientCertificateNativePromptArmStatus.Armed, second.Response.Status);
    }

    [Fact]
    public async Task ArmAsync_ConsumedNonceHistoryRemainsBounded()
    {
        var server = CreateServer(new RecordingExecutor());
        for (var index = 0; index <= WindowsNativePromptBrokerServer.ConsumedAttemptCapacity; index++)
        {
            await using var arm = await server.ArmAsync(
                ValidIdentity(),
                Request(new NativePromptAttemptId(index + 1, index + 10_000, null)));
            Assert.Equal(ClientCertificateNativePromptArmStatus.Armed, arm.Response.Status);
            _ = await arm.Attempt!.ExecuteAsync();
        }

        Assert.Equal(
            WindowsNativePromptBrokerServer.ConsumedAttemptCapacity,
            server.ConsumedAttemptCount);
    }

    [Fact]
    public async Task ArmAsync_ConcurrentAttemptIsRejectedWithoutConsumingItsCycle()
    {
        var executor = new RecordingExecutor();
        var server = CreateServer(executor);
        await using var active = await server.ArmAsync(ValidIdentity(), Request());

        await using var busy = await server.ArmAsync(
            ValidIdentity(),
            Request(new NativePromptAttemptId(8, 1, null)));

        Assert.Equal(ClientCertificateNativePromptArmStatus.BrokerUnavailable, busy.Response.Status);
        Assert.Null(busy.Attempt);
        _ = await active.Attempt!.ExecuteAsync();

        await using var retry = await server.ArmAsync(
            ValidIdentity(),
            Request(new NativePromptAttemptId(8, 2, null)));
        Assert.Equal(ClientCertificateNativePromptArmStatus.Armed, retry.Response.Status);
    }

    [Theory]
    [InlineData("S-1-5-21-999", 4, true)]
    [InlineData("S-1-5-21-123", 9, true)]
    [InlineData("S-1-5-21-123", 4, false)]
    public async Task ArmAsync_WrongClientIdentityIsRejected(
        string sid,
        int sessionId,
        bool interactive)
    {
        var server = CreateServer(new RecordingExecutor());

        await using var result = await server.ArmAsync(
            ValidIdentity() with
            {
                UserSid = sid,
                SessionId = sessionId,
                IsInteractive = interactive
            },
            Request());

        Assert.Equal(ClientCertificateNativePromptArmStatus.PeerIdentityMismatch, result.Response.Status);
        Assert.Null(result.Attempt);
    }

    [Fact]
    public async Task ArmAsync_MissingTrustedClientProcessIdIsRejectedWithoutExecution()
    {
        var executor = new RecordingExecutor();
        var server = CreateServer(executor);

        await using var result = await server.ArmAsync(
            ValidIdentity() with { ProcessId = 0 },
            Request());

        Assert.Equal(ClientCertificateNativePromptArmStatus.PeerIdentityMismatch, result.Response.Status);
        Assert.Null(result.Attempt);
        Assert.Equal(0, executor.CallCount);
    }

    [Theory]
    [InlineData(@"C:\Other\UsbWebLauncher.App.exe", ClientProcessHash)]
    [InlineData(ClientProcessPath, OtherHash)]
    public async Task ArmAsync_WrongClientProcessPathOrHashIsRejected(
        string processPath,
        string processSha256)
    {
        var server = CreateServer(new RecordingExecutor());

        await using var result = await server.ArmAsync(
            ValidIdentity() with
            {
                ProcessPath = processPath,
                ProcessSha256 = processSha256
            },
            Request());

        Assert.Equal(ClientCertificateNativePromptArmStatus.PeerIdentityMismatch, result.Response.Status);
        Assert.Null(result.Attempt);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("core")]
    [InlineData("windows")]
    public async Task ArmAsync_WrongClientManagedAssemblyHashIsRejected(string assembly)
    {
        var identity = ValidIdentity();
        identity = assembly switch
        {
            "application" => identity with { ApplicationAssemblySha256 = OtherHash },
            "core" => identity with { CoreAssemblySha256 = OtherHash },
            "windows" => identity with { WindowsAssemblySha256 = OtherHash },
            _ => throw new ArgumentOutOfRangeException(nameof(assembly))
        };
        var server = CreateServer(new RecordingExecutor());

        await using var result = await server.ArmAsync(identity, Request());

        Assert.Equal(ClientCertificateNativePromptArmStatus.PeerIdentityMismatch, result.Response.Status);
        Assert.Null(result.Attempt);
    }

    [Theory]
    [InlineData("other-policy", ValidHash)]
    [InlineData("policy-v1", "not-a-hash")]
    [InlineData("policy-v1", OtherHash)]
    public async Task ArmAsync_MalformedOrMismatchedPolicyIsRejected(string policyId, string policyHash)
    {
        var server = CreateServer(new RecordingExecutor());

        await using var result = await server.ArmAsync(
            ValidIdentity(),
            Request(policyId: policyId, policyHash: policyHash));

        Assert.Equal(ClientCertificateNativePromptArmStatus.PolicyMismatch, result.Response.Status);
        Assert.Null(result.Attempt);
    }

    [Fact]
    public async Task StopRejectsNewAttemptsAndCancelsActiveAttempt()
    {
        var executor = new RecordingExecutor();
        var server = CreateServer(executor);
        await using var active = await server.ArmAsync(ValidIdentity(), Request());

        await server.StopAsync();
        var completion = await active.Attempt!.ExecuteAsync();
        await using var later = await server.ArmAsync(
            ValidIdentity(),
            Request(new NativePromptAttemptId(8, 1, null)));

        Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, completion.Status);
        Assert.Equal(ClientCertificateNativePromptArmStatus.BrokerUnavailable, later.Response.Status);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task ExecuteUntilDisconnectAsync_ClientDisconnectCancelsBlockingAttempt()
    {
        var executor = new BlockingExecutor();
        var server = CreateServer(executor);
        await using var arm = await server.ArmAsync(ValidIdentity(), Request());
        await using var disconnected = new ImmediateDisconnectStream();

        var completion = await WindowsNativePromptBrokerHost.ExecuteUntilDisconnectAsync(
            arm.Attempt!,
            disconnected,
            CancellationToken.None);

        Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, completion.Status);
        Assert.True(executor.CancellationObserved);
        Assert.True(executor.CleanupCompleted);
    }

    [Fact]
    public async Task ExecuteUntilDisconnectAsync_PostArmedProtocolByteCancelsAndAwaitsExecutorCleanup()
    {
        var executor = new BlockingExecutor();
        var server = CreateServer(executor);
        await using var arm = await server.ArmAsync(ValidIdentity(), Request());
        await using var violation = new ImmediateProtocolByteStream();

        var completion = await WindowsNativePromptBrokerHost.ExecuteUntilDisconnectAsync(
            arm.Attempt!,
            violation,
            CancellationToken.None);

        Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, completion.Status);
        Assert.True(executor.CleanupCompleted);
    }

    [Fact]
    public async Task DisposingArmedAttemptWithoutExecutionReleasesSingleAttemptGate()
    {
        var server = CreateServer(new RecordingExecutor());
        var abandoned = await server.ArmAsync(ValidIdentity(), Request());
        await abandoned.DisposeAsync();

        await using var next = await server.ArmAsync(
            ValidIdentity(),
            Request(new NativePromptAttemptId(8, 1, null)));

        Assert.Equal(ClientCertificateNativePromptArmStatus.Armed, next.Response.Status);
    }

    private const string ValidHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string OtherHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string ClientProcessPath = @"C:\Release\UsbWebLauncher.App.exe";
    private const string ClientProcessHash = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    private const string ClientApplicationHash = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";
    private const string ClientCoreHash = "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE";
    private const string ClientWindowsHash = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    private static WindowsNativePromptBrokerServer CreateServer(INativePromptExecutor executor) =>
        new(
            new NativePromptBrokerPolicy(
                ValidHash,
                Policy(),
                new NativePromptBrokerTrustedClient(
                    ClientProcessPath,
                    ClientProcessHash,
                    ClientApplicationHash,
                    ClientCoreHash,
                    ClientWindowsHash)),
            "S-1-5-21-123",
            4,
            executor);

    private static NativePromptBrokerClientIdentity ValidIdentity() =>
        new(
            "S-1-5-21-123",
            4,
            IsInteractive: true,
            ClientProcessPath,
            ClientProcessHash,
            ClientApplicationHash,
            ClientCoreHash,
            ClientWindowsHash,
            ProcessId: 2468);

    private static NativePromptArmRequest Request(
        NativePromptAttemptId? attempt = null,
        string policyId = "policy-v1",
        string policyHash = ValidHash,
        char[]? pin = null) =>
        new(policyId, policyHash, attempt ?? new NativePromptAttemptId(7, 1, 3), pin ?? RandomPin());

    private static char[] RandomPin() => $"test-{Guid.NewGuid():N}".ToCharArray();

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string WritePolicyFile(
        ClientCertificateNativePromptOptions prompt,
        bool includeSecretField = false)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"native-prompt-policy-{Guid.NewGuid():N}.json");
        var json = includeSecretField
            ? JsonSerializer.Serialize(new
            {
                ProtocolVersion = NativePromptProtocol.ProtocolVersion,
                PolicyHash = ValidHash,
                Prompt = prompt,
                Pin = "random-test-value"
            })
            : JsonSerializer.Serialize(new NativePromptBrokerPolicyDocument(
                NativePromptProtocol.ProtocolVersion,
                ValidHash,
                prompt));
        File.WriteAllText(path, json);
        return path;
    }

    private static ClientCertificateNativePromptOptions Policy() => new(
        true,
        "UsbWebLauncher.NativePromptBroker.v1",
        "policy-v1",
        @"C:\Program Files (x86)\Vendor\Prompt.exe",
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
        "Vendor prompt",
        "#32770",
        ["Shell Embedding", "Internet Explorer_Server"],
        1000);

    private sealed class RecordingExecutor : INativePromptExecutor
    {
        public int CallCount { get; private set; }

        public int ObservedTrustedClientProcessId { get; private set; }

        public ValueTask<ClientCertificateNativePromptResult> ExecuteAsync(
            ClientCertificateNativePromptOptions policy,
            ClearableSecretBuffer ownedPin,
            int trustedClientProcessId,
            CancellationToken cancellationToken = default)
        {
            using (ownedPin)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromResult(
                        new ClientCertificateNativePromptResult(ClientCertificateNativePromptStatus.Cancelled));
                }

                CallCount++;
                ObservedTrustedClientProcessId = trustedClientProcessId;
                return ValueTask.FromResult(
                    new ClientCertificateNativePromptResult(ClientCertificateNativePromptStatus.Success));
            }
        }
    }

    private sealed class RecordingClientInspector(
        NativePromptBrokerClientIdentity identity,
        List<string>? events = null)
        : INativePromptBrokerClientInspector
    {
        private int _inspectionCount;

        public TaskCompletionSource Inspected { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource InspectedTwice { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public NativePromptBrokerClientIdentity? Inspect(NamedPipeServerStream connection)
        {
            events?.Add("Inspect");
            var count = Interlocked.Increment(ref _inspectionCount);
            if (count >= 1)
            {
                Inspected.TrySetResult();
            }
            if (count >= 2)
            {
                InspectedTwice.TrySetResult();
            }
            return identity;
        }
    }

    private sealed class RecordingHostProtocol(List<string>? events = null)
        : INativePromptBrokerHostProtocol
    {
        public int ReadHealthCount { get; private set; }

        public ValueTask ReadHealthRequestAsync(Stream stream, CancellationToken cancellationToken)
        {
            events?.Add("ReadHealth");
            ReadHealthCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteHealthResponseAsync(
            Stream stream,
            NativePromptBrokerHealthResult response,
            CancellationToken cancellationToken)
        {
            events?.Add("WriteHealth");
            return ValueTask.CompletedTask;
        }

        public ValueTask<NativePromptArmRequest> ReadArmRequestAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            events?.Add("ReadArm");
            throw new EndOfStreamException();
        }

        public ValueTask WriteArmResponseAsync(
            Stream stream,
            NativePromptArmResponse response,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask WriteCompletionAsync(
            Stream stream,
            NativePromptCompletionResponse response,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingUpdateFinalizer(List<string> events)
        : INativePromptBrokerUpdateFinalizer
    {
        public TaskCompletionSource Finalized { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask FinalizeAsync(CancellationToken cancellationToken)
        {
            events.Add("Finalize");
            Finalized.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingExecutor : INativePromptExecutor
    {
        public bool CancellationObserved { get; private set; }
        public bool CleanupCompleted { get; private set; }

        public async ValueTask<ClientCertificateNativePromptResult> ExecuteAsync(
            ClientCertificateNativePromptOptions policy,
            ClearableSecretBuffer ownedPin,
            int trustedClientProcessId,
            CancellationToken cancellationToken = default)
        {
            using (ownedPin)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new(ClientCertificateNativePromptStatus.Success);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved = true;
                    return new(ClientCertificateNativePromptStatus.Cancelled);
                }
                finally
                {
                    CleanupCompleted = true;
                }
            }
        }
    }

    private sealed class ImmediateProtocolByteStream : ImmediateDisconnectStream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer[offset] = 1;
            return 1;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }
    }

    private class ImmediateDisconnectStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);
    }
}
