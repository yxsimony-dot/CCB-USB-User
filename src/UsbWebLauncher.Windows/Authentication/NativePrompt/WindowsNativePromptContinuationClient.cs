using System.IO.Pipes;
using System.Security.Principal;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal interface INativePromptClientConnection : IAsyncDisposable
{
    Stream Stream { get; }
}

internal interface INativePromptConnectionFactory
{
    ValueTask<INativePromptClientConnection> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken);
}

internal interface INativePromptPeerInspector
{
    ValueTask<NativePromptPeerInspectionStatus> InspectAsync(
        INativePromptClientConnection connection,
        CancellationToken cancellationToken);
}

internal interface INativePromptClientProtocol
{
    ValueTask<NativePromptArmResponse> ArmAsync(
        INativePromptClientConnection connection,
        string policyId,
        string policyHash,
        NativePromptAttemptId attemptId,
        char[] pinBuffer,
        CancellationToken cancellationToken);

    Task<NativePromptCompletionResponse> ReadCompletionAsync(
        INativePromptClientConnection connection,
        CancellationToken cancellationToken);
}

internal sealed class NativePromptClientProtocol : INativePromptClientProtocol
{
    public async ValueTask<NativePromptArmResponse> ArmAsync(
        INativePromptClientConnection connection,
        string policyId,
        string policyHash,
        NativePromptAttemptId attemptId,
        char[] pinBuffer,
        CancellationToken cancellationToken)
    {
        await NativePromptProtocol.WriteHealthRequestAsync(
            connection.Stream,
            cancellationToken).ConfigureAwait(false);
        var health = await NativePromptProtocol.ReadHealthResponseAsync(
            connection.Stream,
            cancellationToken).ConfigureAwait(false);
        if (!health.IsHealthy || health.ProtocolVersion != NativePromptProtocol.ProtocolVersion)
        {
            throw new InvalidDataException("Native prompt broker health or protocol version mismatch.");
        }

        await NativePromptProtocol.WriteArmRequestAsync(
            connection.Stream,
            policyId,
            policyHash,
            attemptId,
            pinBuffer,
            cancellationToken).ConfigureAwait(false);
        return await NativePromptProtocol.ReadArmResponseAsync(
            connection.Stream,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NativePromptCompletionResponse> ReadCompletionAsync(
        INativePromptClientConnection connection,
        CancellationToken cancellationToken) =>
        await NativePromptProtocol.ReadCompletionAsync(
            connection.Stream,
            cancellationToken).ConfigureAwait(false);
}

internal sealed class WindowsNamedPipeConnectionFactory : INativePromptConnectionFactory
{
    public async ValueTask<INativePromptClientConnection> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new WindowsNamedPipeClientConnection(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class WindowsNamedPipeClientConnection(NamedPipeClientStream pipe)
    : INativePromptClientConnection
{
    private NamedPipeClientStream? _pipe = pipe;

    internal NamedPipeClientStream Pipe =>
        Volatile.Read(ref _pipe) ?? throw new ObjectDisposedException(nameof(WindowsNamedPipeClientConnection));

    public Stream Stream => Pipe;

    public async ValueTask DisposeAsync()
    {
        var current = Interlocked.Exchange(ref _pipe, null);
        if (current is not null)
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class WindowsNativePromptContinuationClient : IClientCertificateNativePromptContinuation
{
    private readonly INativePromptConnectionFactory _connectionFactory;
    private readonly INativePromptPeerInspector _peerInspector;
    private readonly INativePromptClientProtocol _protocol;

    public WindowsNativePromptContinuationClient()
        : this(
            new WindowsNamedPipeConnectionFactory(),
            new WindowsNamedPipePeerInspector(),
            new NativePromptClientProtocol())
    {
    }

    internal WindowsNativePromptContinuationClient(
        INativePromptConnectionFactory connectionFactory,
        INativePromptPeerInspector peerInspector,
        INativePromptClientProtocol protocol)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _peerInspector = peerInspector ?? throw new ArgumentNullException(nameof(peerInspector));
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
    }

    public async ValueTask<ClientCertificateNativePromptArmResult> ArmAsync(
        ClientCertificateAuthenticationOptions authentication,
        AuthenticationAttempt attempt,
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(pin);

        var options = authentication.NativePromptContinuation;
        if (options is null || !options.Enabled ||
            string.IsNullOrWhiteSpace(options.BrokerPipeName) ||
            string.IsNullOrWhiteSpace(options.PolicyId) ||
            options.TimeoutMilliseconds <= 0)
        {
            return new(ClientCertificateNativePromptArmStatus.PolicyMismatch);
        }

        if (pin.AsMemory().Length > NativePromptProtocol.MaxPinCharacters)
        {
            return new(ClientCertificateNativePromptArmStatus.ProtocolMismatch);
        }

        var armCancellation = new ArmCancellationScope(
            cancellationToken,
            TimeSpan.FromMilliseconds(options.TimeoutMilliseconds));
        INativePromptClientConnection? connection = null;
        var ownershipTransferred = false;
        try
        {
            connection = await _connectionFactory.ConnectAsync(
                options.BrokerPipeName,
                armCancellation.Token).ConfigureAwait(false);

            NativePromptPeerInspectionStatus inspection;
            try
            {
                inspection = await _peerInspector.InspectAsync(
                    connection,
                    armCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (armCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new(ClientCertificateNativePromptArmStatus.PeerIdentityMismatch);
            }

            if (inspection != NativePromptPeerInspectionStatus.Verified)
            {
                return new(ClientCertificateNativePromptArmStatus.PeerIdentityMismatch);
            }

            var attemptId = NativePromptAttemptId.Create(attempt);
            var pinBuffer = pin.AsMemory().ToArray();
            NativePromptArmResponse response;
            try
            {
                response = await _protocol.ArmAsync(
                    connection,
                    options.PolicyId,
                    authentication.GetNormalizedHash(),
                    attemptId,
                    pinBuffer,
                    armCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                Array.Clear(pinBuffer, 0, pinBuffer.Length);
            }

            if (response.AttemptId != attemptId)
            {
                return new(ClientCertificateNativePromptArmStatus.ProtocolMismatch);
            }

            if (response.Status != ClientCertificateNativePromptArmStatus.Armed)
            {
                return new(response.Status, PublicErrorCode: response.PublicErrorCode);
            }

            var armedAttempt = new WindowsNativePromptAttempt(
                connection,
                _protocol,
                attemptId,
                armCancellation);
            ownershipTransferred = true;
            if (!armCancellation.TryAccept(out var cancellationStatus))
            {
                await armedAttempt.DisposeAsync().ConfigureAwait(false);
                return new(cancellationStatus);
            }

            return new(
                ClientCertificateNativePromptArmStatus.Armed,
                armedAttempt,
                response.PublicErrorCode);
        }
        catch (OperationCanceledException) when (armCancellation.IsCancellationRequested)
        {
            return new(armCancellation.ArmStatus);
        }
        catch (Exception exception) when (
            armCancellation.IsCancellationRequested &&
            exception is IOException or ObjectDisposedException)
        {
            return new(armCancellation.ArmStatus);
        }
        catch (InvalidDataException)
        {
            return new(ClientCertificateNativePromptArmStatus.ProtocolMismatch);
        }
        catch (IOException)
        {
            return new(ClientCertificateNativePromptArmStatus.BrokerUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return new(ClientCertificateNativePromptArmStatus.BrokerUnavailable);
        }
        catch
        {
            return new(ClientCertificateNativePromptArmStatus.UnknownFailure);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                if (connection is not null)
                {
                    await SafeDisposeAsync(connection).ConfigureAwait(false);
                }

                armCancellation.Dispose();
            }
        }
    }

    private static async ValueTask SafeDisposeAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private sealed class ArmCancellationScope : IDisposable
    {
        private const int AcceptancePending = 0;
        private const int AcceptanceAccepted = 1;
        private const int AcceptanceRejectedByCaller = 2;
        private const int AcceptanceRejectedByTimeout = 3;
        private const int CauseNone = 0;
        private const int CauseCaller = 1;
        private const int CauseTimeout = 2;

        private readonly CancellationToken _callerToken;
        private readonly CancellationToken _timeoutToken;
        private readonly CancellationToken _linkedToken;
        private readonly CancellationTokenSource _timeoutCancellation;
        private readonly CancellationTokenSource _linkedCancellation;
        private readonly CancellationTokenRegistration _callerRegistration;
        private readonly CancellationTokenRegistration _timeoutRegistration;
        private int _acceptanceState;
        private int _firstCause;
        private int _disposed;

        public ArmCancellationScope(CancellationToken callerToken, TimeSpan timeout)
        {
            _callerToken = callerToken;
            _timeoutCancellation = new CancellationTokenSource();
            _timeoutToken = _timeoutCancellation.Token;
            _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                callerToken,
                _timeoutToken);
            _linkedToken = _linkedCancellation.Token;
            _callerRegistration = callerToken.Register(
                static state => ((ArmCancellationScope)state!).RecordCancellation(CauseCaller),
                this);
            _timeoutRegistration = _timeoutToken.Register(
                static state => ((ArmCancellationScope)state!).RecordCancellation(CauseTimeout),
                this);
            _timeoutCancellation.CancelAfter(timeout);
        }

        public CancellationToken Token => _linkedToken;

        public bool IsCancellationRequested => _linkedToken.IsCancellationRequested;

        public ClientCertificateNativePromptArmStatus ArmStatus
        {
            get
            {
                ObserveCancellationSources();
                var acceptanceState = Volatile.Read(ref _acceptanceState);
                if (acceptanceState == AcceptanceRejectedByCaller)
                {
                    return ClientCertificateNativePromptArmStatus.Cancelled;
                }

                if (acceptanceState == AcceptanceRejectedByTimeout)
                {
                    return ClientCertificateNativePromptArmStatus.TimedOut;
                }

                return Volatile.Read(ref _firstCause) == CauseCaller
                    ? ClientCertificateNativePromptArmStatus.Cancelled
                    : ClientCertificateNativePromptArmStatus.TimedOut;
            }
        }

        public bool TryAccept(out ClientCertificateNativePromptArmStatus cancellationStatus)
        {
            ObserveCancellationSources();
            var priorState = Interlocked.CompareExchange(
                ref _acceptanceState,
                AcceptanceAccepted,
                AcceptancePending);
            if (priorState is AcceptancePending or AcceptanceAccepted)
            {
                cancellationStatus = default;
                return true;
            }

            cancellationStatus = priorState == AcceptanceRejectedByCaller
                ? ClientCertificateNativePromptArmStatus.Cancelled
                : ClientCertificateNativePromptArmStatus.TimedOut;
            return false;
        }

        public void CancelTransport() => _linkedCancellation.Cancel();

        public ClientCertificateNativePromptResult CompletionResult(bool disposed) =>
            new(
                disposed || ArmStatus == ClientCertificateNativePromptArmStatus.Cancelled
                    ? ClientCertificateNativePromptStatus.Cancelled
                    : ClientCertificateNativePromptStatus.TimedOut);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _callerRegistration.Dispose();
                _timeoutRegistration.Dispose();
                _linkedCancellation.Dispose();
                _timeoutCancellation.Dispose();
            }
        }

        private void ObserveCancellationSources()
        {
            if (_callerToken.IsCancellationRequested)
            {
                RecordCancellation(CauseCaller);
            }

            if (_timeoutToken.IsCancellationRequested)
            {
                RecordCancellation(CauseTimeout);
            }
        }

        private void RecordCancellation(int cause)
        {
            var rejectionState = cause == CauseCaller
                ? AcceptanceRejectedByCaller
                : AcceptanceRejectedByTimeout;
            var priorState = Interlocked.CompareExchange(
                ref _acceptanceState,
                rejectionState,
                AcceptancePending);
            if (priorState is AcceptancePending or AcceptanceAccepted)
            {
                Interlocked.CompareExchange(ref _firstCause, cause, CauseNone);
            }
        }
    }

    private sealed class WindowsNativePromptAttempt : IClientCertificateNativePromptAttempt
    {
        private readonly INativePromptClientConnection _connection;
        private readonly INativePromptClientProtocol _protocol;
        private readonly NativePromptAttemptId _attemptId;
        private readonly ArmCancellationScope _cancellation;
        private int _disposed;

        public WindowsNativePromptAttempt(
            INativePromptClientConnection connection,
            INativePromptClientProtocol protocol,
            NativePromptAttemptId attemptId,
            ArmCancellationScope cancellation)
        {
            _connection = connection;
            _protocol = protocol;
            _attemptId = attemptId;
            _cancellation = cancellation;
            Completion = CompleteAsync();
        }

        public Task<ClientCertificateNativePromptResult> Completion { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    _cancellation.CancelTransport();
                }
                catch (ObjectDisposedException)
                {
                }

                await SafeDisposeAsync(_connection).ConfigureAwait(false);
            }

            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task<ClientCertificateNativePromptResult> CompleteAsync()
        {
            try
            {
                var response = await _protocol.ReadCompletionAsync(
                    _connection,
                    _cancellation.Token).ConfigureAwait(false);
                return response.AttemptId == _attemptId
                    ? new(response.Status, response.PublicErrorCode)
                    : new(ClientCertificateNativePromptStatus.ProtocolMismatch);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                return CancellationResult();
            }
            catch (Exception exception) when (
                _cancellation.IsCancellationRequested &&
                exception is IOException or ObjectDisposedException)
            {
                return CancellationResult();
            }
            catch (InvalidDataException)
            {
                return new(ClientCertificateNativePromptStatus.ProtocolMismatch);
            }
            catch (IOException)
            {
                return new(ClientCertificateNativePromptStatus.BrokerUnavailable);
            }
            catch
            {
                return new(ClientCertificateNativePromptStatus.UnknownFailure);
            }
            finally
            {
                await SafeDisposeAsync(_connection).ConfigureAwait(false);
                _cancellation.Dispose();
            }
        }

        private ClientCertificateNativePromptResult CancellationResult() =>
            _cancellation.CompletionResult(Volatile.Read(ref _disposed) != 0);
    }
}
