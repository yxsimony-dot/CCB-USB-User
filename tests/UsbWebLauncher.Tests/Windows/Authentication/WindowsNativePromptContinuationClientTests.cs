// Certificate identities and key-container identifiers in this file are synthetic public fixtures.
using System.IO;
using System.Security.Cryptography;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsNativePromptContinuationClientTests
{
    [Fact]
    public async Task ArmAsync_VerifiesPeerBeforeSendingCopiedPinAndReturnsBeforeCompletion()
    {
        var events = new List<string>();
        var connection = new RecordingConnection();
        var factory = new RecordingConnectionFactory(connection, events);
        var inspector = new RecordingPeerInspector(NativePromptPeerInspectionStatus.Verified, events);
        var secret = CreateRandomSecret(24);
        var protocol = new RecordingClientProtocol(events)
        {
            HoldCompletionOpen = true,
            ExpectedPin = secret
        };
        var client = new WindowsNativePromptContinuationClient(factory, inspector, protocol);
        var authentication = CreateAuthentication();
        var attempt = new AuthenticationAttempt(17, 29, 41);

        try
        {
            using var pin = ClientCertificatePin.CopyOf(secret.AsMemory());

            var result = await client.ArmAsync(authentication, attempt, pin);

            Assert.Equal(ClientCertificateNativePromptArmStatus.Armed, result.Status);
            Assert.NotNull(result.Attempt);
            Assert.False(result.Attempt!.Completion.IsCompleted);
            Assert.Equal(["Connect", "Inspect", "Arm", "Completion"], events);
            Assert.Equal(authentication.NativePromptContinuation!.BrokerPipeName, factory.PipeName);
            Assert.Equal(authentication.NativePromptContinuation.PolicyId, protocol.PolicyId);
            Assert.Equal(authentication.GetNormalizedHash(), protocol.PolicyHash);
            Assert.Equal(41, protocol.AttemptId.BrowserOperationId);
            Assert.False(protocol.AttemptId.NonceHigh == 0 && protocol.AttemptId.NonceLow == 0);
            Assert.True(protocol.PinMatchedDuringArm);
            Assert.NotNull(protocol.CapturedPinBuffer);
            Assert.All(protocol.CapturedPinBuffer!, value => Assert.Equal('\0', value));

            await result.Attempt.DisposeAsync();
            Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, (await result.Attempt.Completion).Status);
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ArmAsync_RejectsUnverifiedServerBeforeWritingAnyProtocolBytes(
        int inspectionStatus)
    {
        var events = new List<string>();
        var connection = new RecordingConnection();
        var protocol = new RecordingClientProtocol(events);
        var client = new WindowsNativePromptContinuationClient(
            new RecordingConnectionFactory(connection, events),
            new RecordingPeerInspector((NativePromptPeerInspectionStatus)inspectionStatus, events),
            protocol);
        using var pin = CreatePin();

        var result = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin);

        Assert.Equal(ClientCertificateNativePromptArmStatus.PeerIdentityMismatch, result.Status);
        Assert.Null(result.Attempt);
        Assert.Equal(["Connect", "Inspect"], events);
        Assert.Null(protocol.CapturedPinBuffer);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ArmAsync_InspectionExceptionFailsClosedBeforeWriting()
    {
        var events = new List<string>();
        var connection = new RecordingConnection();
        var protocol = new RecordingClientProtocol(events);
        var client = new WindowsNativePromptContinuationClient(
            new RecordingConnectionFactory(connection, events),
            new ThrowingPeerInspector(events),
            protocol);
        using var pin = CreatePin();

        var result = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin);

        Assert.Equal(ClientCertificateNativePromptArmStatus.PeerIdentityMismatch, result.Status);
        Assert.Equal(["Connect", "Inspect"], events);
        Assert.Null(protocol.CapturedPinBuffer);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Theory]
    [InlineData(ClientCertificateNativePromptArmStatus.PolicyMismatch)]
    [InlineData(ClientCertificateNativePromptArmStatus.Replayed)]
    [InlineData(ClientCertificateNativePromptArmStatus.ProtocolMismatch)]
    [InlineData(ClientCertificateNativePromptArmStatus.UnknownFailure)]
    public async Task ArmAsync_ClassifiesExplicitBrokerFailureAndDoesNotCreateAttempt(
        ClientCertificateNativePromptArmStatus brokerStatus)
    {
        var connection = new RecordingConnection();
        var protocol = new RecordingClientProtocol { ArmStatus = brokerStatus };
        var client = CreateClient(connection, protocol);
        using var pin = CreatePin();

        var result = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin);

        Assert.Equal(brokerStatus, result.Status);
        Assert.Null(result.Attempt);
        Assert.NotNull(protocol.CapturedPinBuffer);
        Assert.All(protocol.CapturedPinBuffer!, value => Assert.Equal('\0', value));
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ArmAsync_RejectsMismatchedResponseAttemptAsProtocolFailure()
    {
        var connection = new RecordingConnection();
        var protocol = new RecordingClientProtocol { ResponseAttemptId = new(999, 2, 3) };
        var client = CreateClient(connection, protocol);
        using var pin = CreatePin();

        var result = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin);

        Assert.Equal(ClientCertificateNativePromptArmStatus.ProtocolMismatch, result.Status);
        Assert.Null(result.Attempt);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Theory]
    [MemberData(nameof(ArmFailureCases))]
    public async Task ArmAsync_ClassifiesTimeoutCancellationDisconnectAndProtocolFailure(
        Exception failure,
        ClientCertificateNativePromptArmStatus expectedStatus)
    {
        var connection = new RecordingConnection();
        var protocol = new RecordingClientProtocol { ArmException = failure };
        var client = CreateClient(connection, protocol);
        using var pin = CreatePin();

        var result = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Attempt);
        Assert.NotNull(protocol.CapturedPinBuffer);
        Assert.All(protocol.CapturedPinBuffer!, value => Assert.Equal('\0', value));
        Assert.Equal(1, connection.DisposeCount);
    }

    public static TheoryData<Exception, ClientCertificateNativePromptArmStatus> ArmFailureCases => new()
    {
        { new IOException(), ClientCertificateNativePromptArmStatus.BrokerUnavailable },
        { new EndOfStreamException(), ClientCertificateNativePromptArmStatus.BrokerUnavailable },
        { new InvalidDataException(), ClientCertificateNativePromptArmStatus.ProtocolMismatch },
        { new UnauthorizedAccessException(), ClientCertificateNativePromptArmStatus.BrokerUnavailable },
        { new InvalidOperationException(), ClientCertificateNativePromptArmStatus.UnknownFailure }
    };

    [Fact]
    public async Task ArmAsync_ConnectTimeoutIsClassifiedWithoutLeakingCancellation()
    {
        var factory = new BlockingConnectionFactory();
        var client = new WindowsNativePromptContinuationClient(
            factory,
            new RecordingPeerInspector(NativePromptPeerInspectionStatus.Verified),
            new RecordingClientProtocol());
        using var pin = CreatePin();

        var result = await client.ArmAsync(
            CreateAuthentication(timeoutMilliseconds: 25),
            CreateAttempt(),
            pin);

        Assert.Equal(ClientCertificateNativePromptArmStatus.TimedOut, result.Status);
        Assert.Null(result.Attempt);
    }

    [Fact]
    public async Task ArmAsync_CallerCancellationIsClassifiedWithoutLeakingCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new WindowsNativePromptContinuationClient(
            new BlockingConnectionFactory(),
            new RecordingPeerInspector(NativePromptPeerInspectionStatus.Verified),
            new RecordingClientProtocol());
        using var pin = CreatePin();

        var result = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin, cancellation.Token);

        Assert.Equal(ClientCertificateNativePromptArmStatus.Cancelled, result.Status);
        Assert.Null(result.Attempt);
    }

    [Fact]
    public async Task ArmAsync_CallerCancellationWinningDisconnectIsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var protocol = new RecordingClientProtocol
        {
            CancelDuringArm = cancellation,
            ArmException = new IOException()
        };
        var client = CreateClient(new RecordingConnection(), protocol);
        using var pin = CreatePin();

        var result = await client.ArmAsync(
            CreateAuthentication(),
            CreateAttempt(),
            pin,
            cancellation.Token);

        Assert.Equal(ClientCertificateNativePromptArmStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task ArmAsync_CallerCancellationAfterArmedResponseBeforeTransferDoesNotAuthorize()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new RecordingConnection();
        var protocol = new RecordingClientProtocol
        {
            HoldCompletionOpen = true,
            AfterArmResponseProduced = _ => cancellation.Cancel()
        };
        var client = CreateClient(connection, protocol);
        using var pin = CreatePin();

        var result = await client.ArmAsync(
            CreateAuthentication(),
            CreateAttempt(),
            pin,
            cancellation.Token);

        Assert.Equal(ClientCertificateNativePromptArmStatus.Cancelled, result.Status);
        Assert.Null(result.Attempt);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ArmAsync_TimeoutAfterArmedResponseBeforeTransferDoesNotAuthorize()
    {
        var connection = new RecordingConnection();
        var protocol = new RecordingClientProtocol
        {
            HoldCompletionOpen = true,
            AfterArmResponseProduced = cancellationToken =>
                Assert.True(cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
        };
        var client = CreateClient(connection, protocol);
        using var pin = CreatePin();

        var result = await client.ArmAsync(
            CreateAuthentication(timeoutMilliseconds: 25),
            CreateAttempt(),
            pin);

        Assert.Equal(ClientCertificateNativePromptArmStatus.TimedOut, result.Status);
        Assert.Null(result.Attempt);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ArmAsync_TimeoutWinningAcceptanceRemainsTimedOutWhenCallerCancelsDuringDisposal()
    {
        using var callerCancellation = new CancellationTokenSource();
        var connection = new RecordingConnection(blockDisposal: true);
        var protocol = new RecordingClientProtocol
        {
            HoldCompletionOpen = true,
            AfterArmResponseProduced = cancellationToken =>
                Assert.True(cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
        };
        var client = CreateClient(connection, protocol);
        using var pin = CreatePin();

        var armTask = client.ArmAsync(
            CreateAuthentication(timeoutMilliseconds: 25),
            CreateAttempt(),
            pin,
            callerCancellation.Token).AsTask();

        try
        {
            await connection.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(armTask.IsCompleted);
            callerCancellation.Cancel();
        }
        finally
        {
            connection.ReleaseDisposal();
        }

        var result = await armTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ClientCertificateNativePromptArmStatus.TimedOut, result.Status);
        Assert.Null(result.Attempt);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task ArmAsync_RejectsOversizedPinBeforeConnecting()
    {
        var factory = new RecordingConnectionFactory(new RecordingConnection());
        var client = new WindowsNativePromptContinuationClient(
            factory,
            new RecordingPeerInspector(NativePromptPeerInspectionStatus.Verified),
            new RecordingClientProtocol());
        var secret = CreateRandomSecret(NativePromptProtocol.MaxPinCharacters + 1);

        try
        {
            using var pin = ClientCertificatePin.CopyOf(secret.AsMemory());
            var result = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin);

            Assert.Equal(ClientCertificateNativePromptArmStatus.ProtocolMismatch, result.Status);
            Assert.Equal(0, factory.ConnectCount);
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
        }
    }

    [Fact]
    public async Task Completion_ReturnsExplicitSuccessAndClosesTransport()
    {
        var connection = new RecordingConnection();
        var protocol = new RecordingClientProtocol
        {
            CompletionStatus = ClientCertificateNativePromptStatus.Success,
            CompletionPublicErrorCode = 37
        };
        var client = CreateClient(connection, protocol);
        using var pin = CreatePin();

        var armed = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin);
        var completion = await armed.Attempt!.Completion;

        Assert.Equal(ClientCertificateNativePromptStatus.Success, completion.Status);
        Assert.Equal(37, completion.PublicErrorCode);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task Completion_ClassifiesReplayResponse()
    {
        var protocol = new RecordingClientProtocol
        {
            CompletionStatus = ClientCertificateNativePromptStatus.Replayed
        };
        var client = CreateClient(new RecordingConnection(), protocol);
        using var pin = CreatePin();

        var armed = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin);

        Assert.Equal(ClientCertificateNativePromptStatus.Replayed, (await armed.Attempt!.Completion).Status);
    }

    [Fact]
    public async Task Completion_RejectsMismatchedAttemptWithoutLeakingException()
    {
        var protocol = new RecordingClientProtocol
        {
            CompletionAttemptId = new(404, 2, 3)
        };
        var client = CreateClient(new RecordingConnection(), protocol);
        using var pin = CreatePin();

        var armed = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin);

        Assert.Equal(ClientCertificateNativePromptStatus.ProtocolMismatch, (await armed.Attempt!.Completion).Status);
    }

    [Theory]
    [MemberData(nameof(CompletionFailureCases))]
    public async Task Completion_ClassifiesTransportAndProtocolFailuresWithoutLeakingExceptions(
        Exception failure,
        ClientCertificateNativePromptStatus expectedStatus)
    {
        var protocol = new RecordingClientProtocol { CompletionException = failure };
        var client = CreateClient(new RecordingConnection(), protocol);
        using var pin = CreatePin();

        var armed = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin);
        var completion = await armed.Attempt!.Completion;

        Assert.Equal(expectedStatus, completion.Status);
    }

    public static TheoryData<Exception, ClientCertificateNativePromptStatus> CompletionFailureCases => new()
    {
        { new IOException(), ClientCertificateNativePromptStatus.BrokerUnavailable },
        { new EndOfStreamException(), ClientCertificateNativePromptStatus.BrokerUnavailable },
        { new InvalidDataException(), ClientCertificateNativePromptStatus.ProtocolMismatch },
        { new InvalidOperationException(), ClientCertificateNativePromptStatus.UnknownFailure }
    };

    [Fact]
    public async Task Completion_TimesOutAndClosesTransport()
    {
        var connection = new RecordingConnection();
        var protocol = new RecordingClientProtocol { HoldCompletionOpen = true };
        var client = CreateClient(connection, protocol);
        using var pin = CreatePin();

        var armed = await client.ArmAsync(CreateAuthentication(timeoutMilliseconds: 500), CreateAttempt(), pin);
        Assert.Equal(ClientCertificateNativePromptArmStatus.Armed, armed.Status);
        var completion = await armed.Attempt!.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ClientCertificateNativePromptStatus.TimedOut, completion.Status);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task Completion_TracksCallerCancellationAfterArmReturns()
    {
        using var cancellation = new CancellationTokenSource();
        var protocol = new RecordingClientProtocol { HoldCompletionOpen = true };
        var client = CreateClient(new RecordingConnection(), protocol);
        using var pin = CreatePin();
        var armed = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin, cancellation.Token);

        cancellation.Cancel();
        var completion = await armed.Attempt!.Completion;

        Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, completion.Status);
    }

    [Fact]
    public async Task Attempt_DisposeCancelsCompletionAndClosesTransportIdempotently()
    {
        var connection = new RecordingConnection();
        var protocol = new RecordingClientProtocol { HoldCompletionOpen = true };
        var client = CreateClient(connection, protocol);
        using var pin = CreatePin();
        var armed = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin);

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => armed.Attempt!.DisposeAsync().AsTask()));
        await armed.Attempt!.DisposeAsync();
        var completion = await armed.Attempt.Completion;

        Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, completion.Status);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task Attempt_DisposeWinningPipeCloseStillCompletesAsCancelled()
    {
        var connection = new RecordingConnection();
        var protocol = new RecordingClientProtocol
        {
            HoldCompletionOpen = true,
            CancellationFailure = new ObjectDisposedException("pipe")
        };
        var client = CreateClient(connection, protocol);
        using var pin = CreatePin();
        var armed = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin);

        await armed.Attempt!.DisposeAsync();

        Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, (await armed.Attempt.Completion).Status);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task Completion_CallerCancellationWinningDisconnectIsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var protocol = new RecordingClientProtocol
        {
            HoldCompletionOpen = true,
            CancellationFailure = new IOException()
        };
        var client = CreateClient(new RecordingConnection(), protocol);
        using var pin = CreatePin();
        var armed = await client.ArmAsync(CreateAuthentication(), CreateAttempt(), pin, cancellation.Token);

        cancellation.Cancel();

        Assert.Equal(ClientCertificateNativePromptStatus.Cancelled, (await armed.Attempt!.Completion).Status);
    }

    [Fact]
    public async Task Completion_TimeoutWinningDisconnectIsTimedOut()
    {
        var protocol = new RecordingClientProtocol
        {
            HoldCompletionOpen = true,
            CancellationFailure = new IOException()
        };
        var client = CreateClient(new RecordingConnection(), protocol);
        using var pin = CreatePin();
        var armed = await client.ArmAsync(
            CreateAuthentication(timeoutMilliseconds: 500),
            CreateAttempt(),
            pin);

        Assert.Equal(ClientCertificateNativePromptArmStatus.Armed, armed.Status);
        Assert.Equal(
            ClientCertificateNativePromptStatus.TimedOut,
            (await armed.Attempt!.Completion.WaitAsync(TimeSpan.FromSeconds(2))).Status);
    }

    [Fact]
    public async Task PeerInspector_AcceptsOnlyNormalizedInstalledHighIntegrityBrokerPath()
    {
        var expected = @"C:\Program Files\UsbWebLauncher\NativePromptBroker\UsbWebLauncher.NativePromptBroker.exe";
        var native = new RecordingPeerNativeApi
        {
            ServerProcessId = 811,
            ProcessPath = @"c:\PROGRAM FILES\UsbWebLauncher\NativePromptBroker\.\UsbWebLauncher.NativePromptBroker.exe",
            IntegrityRid = WindowsNamedPipePeerInspector.HighIntegrityRid
        };
        var inspector = new WindowsNamedPipePeerInspector(native, @"C:\Program Files");

        var result = await inspector.InspectAsync(new RecordingConnection(), CancellationToken.None);

        Assert.Equal(NativePromptPeerInspectionStatus.Verified, result);
        Assert.Equal(expected, inspector.ExpectedBrokerPath);
        Assert.Equal(["ServerProcessId", "ProcessPath:811", "Integrity:811"], native.Calls);
    }

    [Theory]
    [InlineData(0, @"C:\Program Files\UsbWebLauncher\NativePromptBroker\UsbWebLauncher.NativePromptBroker.exe", 0x3000, 1)]
    [InlineData(811, @"C:\Elsewhere\UsbWebLauncher.NativePromptBroker.exe", 0x3000, 2)]
    [InlineData(811, "relative-broker.exe", 0x3000, 2)]
    [InlineData(811, @"\\server\share\UsbWebLauncher.NativePromptBroker.exe", 0x3000, 2)]
    [InlineData(811, @"C:\Program Files\UsbWebLauncher\NativePromptBroker\UsbWebLauncher.NativePromptBroker.exe", 0x2000, 3)]
    [InlineData(811, @"C:\Program Files\UsbWebLauncher\NativePromptBroker\UsbWebLauncher.NativePromptBroker.exe", 0x4000, 3)]
    public async Task PeerInspector_RejectsMissingPidPathMismatchNonLocalPathAndNonHighIntegrity(
        int processId,
        string processPath,
        int integrityRid,
        int expectedStatus)
    {
        var native = new RecordingPeerNativeApi
        {
            ServerProcessId = processId,
            ProcessPath = processPath,
            IntegrityRid = integrityRid
        };
        var inspector = new WindowsNamedPipePeerInspector(native, @"C:\Program Files");

        var result = await inspector.InspectAsync(new RecordingConnection(), CancellationToken.None);

        Assert.Equal((NativePromptPeerInspectionStatus)expectedStatus, result);
    }

    [Fact]
    public async Task PeerInspector_InspectionFailureIsFailClosed()
    {
        var native = new RecordingPeerNativeApi { Exception = new InvalidOperationException() };
        var inspector = new WindowsNamedPipePeerInspector(native, @"C:\Program Files");

        var result = await inspector.InspectAsync(new RecordingConnection(), CancellationToken.None);

        Assert.Equal(NativePromptPeerInspectionStatus.InspectionFailed, result);
    }

    private static WindowsNativePromptContinuationClient CreateClient(
        RecordingConnection connection,
        RecordingClientProtocol protocol) =>
        new(
            new RecordingConnectionFactory(connection),
            new RecordingPeerInspector(NativePromptPeerInspectionStatus.Verified),
            protocol);

    private static ClientCertificateAuthenticationOptions CreateAuthentication(int timeoutMilliseconds = 10_000) => new(
        "ccb-usb-key",
        "b2b.ccb.com",
        443,
        new("1111111111111111111111111111111111111111", "GSB00000000003", "CCBRSACAROOT"),
        new("CCB HD CSP V3.0", 1, "SAMPLE_KEY_CONTAINER_1_RSA", "AT_KEYEXCHANGE"),
        "UsbWebLauncher/ClientCertificate/ccb-usb-key",
        new("Pkcs11", @"C:\Windows\System32\CCB_HDZB_2G_P11.dll", "SAMPLE_KEY_CONTAINER_1"),
        new(
            true,
            $"UsbWebLauncher.NativePromptBroker.Tests.{Guid.NewGuid():N}",
            "ccb-hdzb-client-certificate-v1",
            @"C:\Program Files (x86)\CCBComponents\Detector\CCBDetector.exe",
            "EDB0029E220ABCC32676B7AA90B2E012C4FB84AF0AD891BB0BA6B0F021FDB607",
            "0123456789ABCDEF0123456789ABCDEF01234567",
            "E路护航网银安全组件",
            "#32770",
            ["Shell Embedding", "Shell DocObject View", "Internet Explorer_Server"],
            timeoutMilliseconds));

    private static AuthenticationAttempt CreateAttempt() => new(1, 2, 3);

    private static ClientCertificatePin CreatePin()
    {
        var secret = CreateRandomSecret(24);
        try
        {
            return ClientCertificatePin.CopyOf(secret.AsMemory());
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
        }
    }

    private static char[] CreateRandomSecret(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        try
        {
            return bytes.Select(value => (char)('!' + value % 90)).ToArray();
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    private sealed class RecordingConnection(bool blockDisposal = false) : INativePromptClientConnection
    {
        private int _disposed;
        private readonly TaskCompletionSource _disposeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowDisposal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public Task DisposeStarted => _disposeStarted.Task;

        public Stream Stream { get; } = Stream.Null;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeCount++;
                _disposeStarted.TrySetResult();
                if (blockDisposal)
                {
                    await _allowDisposal.Task;
                }
            }
        }

        public void ReleaseDisposal() => _allowDisposal.TrySetResult();
    }

    private sealed class RecordingConnectionFactory(
        INativePromptClientConnection connection,
        List<string>? events = null) : INativePromptConnectionFactory
    {
        public int ConnectCount { get; private set; }

        public string? PipeName { get; private set; }

        public ValueTask<INativePromptClientConnection> ConnectAsync(
            string pipeName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            PipeName = pipeName;
            events?.Add("Connect");
            return ValueTask.FromResult(connection);
        }
    }

    private sealed class BlockingConnectionFactory : INativePromptConnectionFactory
    {
        public async ValueTask<INativePromptClientConnection> ConnectAsync(
            string pipeName,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class RecordingPeerInspector(
        NativePromptPeerInspectionStatus status,
        List<string>? events = null) : INativePromptPeerInspector
    {
        public ValueTask<NativePromptPeerInspectionStatus> InspectAsync(
            INativePromptClientConnection connection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events?.Add("Inspect");
            return ValueTask.FromResult(status);
        }
    }

    private sealed class ThrowingPeerInspector(List<string> events) : INativePromptPeerInspector
    {
        public ValueTask<NativePromptPeerInspectionStatus> InspectAsync(
            INativePromptClientConnection connection,
            CancellationToken cancellationToken)
        {
            events.Add("Inspect");
            throw new InvalidOperationException();
        }
    }

    private sealed class RecordingClientProtocol(List<string>? events = null) : INativePromptClientProtocol
    {
        public ClientCertificateNativePromptArmStatus ArmStatus { get; init; } = ClientCertificateNativePromptArmStatus.Armed;
        public int? ArmPublicErrorCode { get; init; }
        public NativePromptAttemptId? ResponseAttemptId { get; init; }
        public Exception? ArmException { get; init; }
        public CancellationTokenSource? CancelDuringArm { get; init; }
        public Action<CancellationToken>? AfterArmResponseProduced { get; init; }
        public ClientCertificateNativePromptStatus CompletionStatus { get; init; } = ClientCertificateNativePromptStatus.Success;
        public int? CompletionPublicErrorCode { get; init; }
        public NativePromptAttemptId? CompletionAttemptId { get; init; }
        public Exception? CompletionException { get; init; }
        public Exception? CancellationFailure { get; init; }
        public bool HoldCompletionOpen { get; init; }
        public string? PolicyId { get; private set; }
        public string? PolicyHash { get; private set; }
        public NativePromptAttemptId AttemptId { get; private set; }
        public char[]? CapturedPinBuffer { get; private set; }
        public char[]? ExpectedPin { get; init; }
        public bool PinMatchedDuringArm { get; private set; }

        public ValueTask<NativePromptArmResponse> ArmAsync(
            INativePromptClientConnection connection,
            string policyId,
            string policyHash,
            NativePromptAttemptId attemptId,
            char[] pinBuffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events?.Add("Arm");
            PolicyId = policyId;
            PolicyHash = policyHash;
            AttemptId = attemptId;
            CapturedPinBuffer = pinBuffer;
            PinMatchedDuringArm = ExpectedPin is not null &&
                ExpectedPin.AsSpan().SequenceEqual(pinBuffer);
            CancelDuringArm?.Cancel();
            if (ArmException is not null)
            {
                throw ArmException;
            }

            var response = new NativePromptArmResponse(
                ResponseAttemptId ?? attemptId,
                ArmStatus,
                ArmPublicErrorCode);
            AfterArmResponseProduced?.Invoke(cancellationToken);
            return ValueTask.FromResult(response);
        }

        public async Task<NativePromptCompletionResponse> ReadCompletionAsync(
            INativePromptClientConnection connection,
            CancellationToken cancellationToken)
        {
            events?.Add("Completion");
            if (HoldCompletionOpen)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (CancellationFailure is not null)
                {
                    throw CancellationFailure;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (CompletionException is not null)
            {
                throw CompletionException;
            }

            return new(
                CompletionAttemptId ?? AttemptId,
                CompletionStatus,
                CompletionPublicErrorCode);
        }
    }

    private sealed class RecordingPeerNativeApi : IWindowsNamedPipePeerNativeApi
    {
        public List<string> Calls { get; } = [];
        public int ServerProcessId { get; init; }
        public string ProcessPath { get; init; } = string.Empty;
        public int IntegrityRid { get; init; }
        public Exception? Exception { get; init; }

        public bool TryGetServerProcessId(INativePromptClientConnection connection, out int processId)
        {
            Calls.Add("ServerProcessId");
            if (Exception is not null)
            {
                throw Exception;
            }

            processId = ServerProcessId;
            return processId > 0;
        }

        public bool TryGetProcessPath(int processId, out string processPath)
        {
            Calls.Add($"ProcessPath:{processId}");
            processPath = ProcessPath;
            return !string.IsNullOrEmpty(processPath);
        }

        public bool TryGetIntegrityRid(int processId, out int integrityRid)
        {
            Calls.Add($"Integrity:{processId}");
            integrityRid = IntegrityRid;
            return true;
        }
    }
}
