using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal interface INativePromptBrokerHealthClient
{
    ValueTask<NativePromptBrokerHealthResult> CheckAsync(
        string pipeName,
        CancellationToken cancellationToken);
}

internal sealed class WindowsNativePromptBrokerHealthClient : INativePromptBrokerHealthClient
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(5);
    private readonly INativePromptConnectionFactory _connectionFactory;
    private readonly INativePromptPeerInspector _peerInspector;

    public WindowsNativePromptBrokerHealthClient()
        : this(new WindowsNamedPipeConnectionFactory(), new WindowsNamedPipePeerInspector())
    {
    }

    internal WindowsNativePromptBrokerHealthClient(
        INativePromptConnectionFactory connectionFactory,
        INativePromptPeerInspector peerInspector)
    {
        _connectionFactory = connectionFactory;
        _peerInspector = peerInspector;
    }

    public async ValueTask<NativePromptBrokerHealthResult> CheckAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(HealthTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        INativePromptClientConnection? connection = null;
        try
        {
            connection = await _connectionFactory.ConnectAsync(
                pipeName,
                linked.Token).ConfigureAwait(false);
            if (await _peerInspector.InspectAsync(connection, linked.Token).ConfigureAwait(false) !=
                NativePromptPeerInspectionStatus.Verified)
            {
                return new(false, 0);
            }

            await NativePromptProtocol.WriteHealthRequestAsync(
                connection.Stream,
                linked.Token).ConfigureAwait(false);
            return await NativePromptProtocol.ReadHealthResponseAsync(
                connection.Stream,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(false, 0);
        }
        finally
        {
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }
}

internal interface INativePromptBrokerClientInspector
{
    NativePromptBrokerClientIdentity? Inspect(NamedPipeServerStream connection);
}

internal sealed class WindowsNativePromptBrokerClientInspector : INativePromptBrokerClientInspector
{
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint TokenQuery = 0x00000008;

    public NativePromptBrokerClientIdentity? Inspect(NamedPipeServerStream connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            if (!GetNamedPipeClientProcessId(connection.SafePipeHandle, out var processId) ||
                processId is 0 or > int.MaxValue)
            {
                return null;
            }

            using var process = OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                processId);
            if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
            {
                return null;
            }

            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                var sid = identity.User?.Value;
                if (string.IsNullOrWhiteSpace(sid) ||
                    !ProcessIdToSessionId(processId, out var sessionId) ||
                    sessionId > int.MaxValue ||
                    !TryGetProcessPath(process, out var processPath) ||
                    !TryComputeSha256(processPath, out var processSha256) ||
                    !TryComputeSiblingSha256(
                        processPath,
                        NativePromptBrokerTrustedClient.ApplicationAssemblyFileName,
                        out var applicationAssemblySha256) ||
                    !TryComputeSiblingSha256(
                        processPath,
                        NativePromptBrokerTrustedClient.CoreAssemblyFileName,
                        out var coreAssemblySha256) ||
                    !TryComputeSiblingSha256(
                        processPath,
                        NativePromptBrokerTrustedClient.WindowsAssemblyFileName,
                        out var windowsAssemblySha256))
                {
                    return null;
                }

                return new(
                    sid,
                    checked((int)sessionId),
                    IsInteractive: true,
                    processPath,
                    processSha256,
                    applicationAssemblySha256,
                    coreAssemblySha256,
                    windowsAssemblySha256,
                    ProcessId: checked((int)processId));
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetProcessPath(SafeProcessHandle process, out string processPath)
    {
        var buffer = new StringBuilder(32_768);
        var length = checked((uint)buffer.Capacity);
        if (!QueryFullProcessImageName(process, 0, buffer, ref length) || length == 0)
        {
            processPath = string.Empty;
            return false;
        }

        processPath = buffer.ToString(0, checked((int)length));
        return Path.IsPathFullyQualified(processPath) &&
               !processPath.StartsWith(@"\\", StringComparison.Ordinal) &&
               !processPath.StartsWith("//", StringComparison.Ordinal);
    }

    private static bool TryComputeSha256(string path, out string sha256)
    {
        sha256 = string.Empty;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            sha256 = Convert.ToHexString(SHA256.HashData(stream));
            return sha256.Length == 64;
        }
        catch
        {
            sha256 = string.Empty;
            return false;
        }
    }

    private static bool TryComputeSiblingSha256(
        string processPath,
        string fileName,
        out string sha256)
    {
        var directory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            sha256 = string.Empty;
            return false;
        }

        return TryComputeSha256(Path.Combine(directory, fileName), out sha256);
    }

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder processPath,
        ref uint size);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(
        uint processId,
        out uint sessionId);
}

internal interface INativePromptBrokerHostProtocol
{
    ValueTask ReadHealthRequestAsync(Stream stream, CancellationToken cancellationToken);

    ValueTask WriteHealthResponseAsync(
        Stream stream,
        NativePromptBrokerHealthResult response,
        CancellationToken cancellationToken);

    ValueTask<NativePromptArmRequest> ReadArmRequestAsync(
        Stream stream,
        CancellationToken cancellationToken);

    ValueTask WriteArmResponseAsync(
        Stream stream,
        NativePromptArmResponse response,
        CancellationToken cancellationToken);

    ValueTask WriteCompletionAsync(
        Stream stream,
        NativePromptCompletionResponse response,
        CancellationToken cancellationToken);
}

internal sealed class NativePromptBrokerHostProtocol : INativePromptBrokerHostProtocol
{
    public ValueTask ReadHealthRequestAsync(Stream stream, CancellationToken cancellationToken) =>
        NativePromptProtocol.ReadHealthRequestAsync(stream, cancellationToken);

    public ValueTask WriteHealthResponseAsync(
        Stream stream,
        NativePromptBrokerHealthResult response,
        CancellationToken cancellationToken) =>
        NativePromptProtocol.WriteHealthResponseAsync(stream, response, cancellationToken);

    public ValueTask<NativePromptArmRequest> ReadArmRequestAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        NativePromptProtocol.ReadArmRequestAsync(stream, cancellationToken);

    public ValueTask WriteArmResponseAsync(
        Stream stream,
        NativePromptArmResponse response,
        CancellationToken cancellationToken) =>
        NativePromptProtocol.WriteArmResponseAsync(stream, response, cancellationToken);

    public ValueTask WriteCompletionAsync(
        Stream stream,
        NativePromptCompletionResponse response,
        CancellationToken cancellationToken) =>
        NativePromptProtocol.WriteCompletionAsync(stream, response, cancellationToken);
}

internal interface INativePromptBrokerUpdateFinalizer
{
    ValueTask FinalizeAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsNativePromptBrokerUpdateFinalizer
    : INativePromptBrokerUpdateFinalizer
{
    private readonly WindowsNativePromptBrokerInstaller _installer = new();

    public ValueTask FinalizeAsync(CancellationToken cancellationToken) =>
        _installer.FinalizeUpdateAsync(cancellationToken);
}

internal sealed class NullNativePromptBrokerUpdateFinalizer
    : INativePromptBrokerUpdateFinalizer
{
    public static NullNativePromptBrokerUpdateFinalizer Instance { get; } = new();

    public ValueTask FinalizeAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

internal static class WindowsNativePromptBrokerPipeFactory
{
    public static NamedPipeServerStream Create(string pipeName)
    {
        var currentUser = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The native prompt broker user SID is unavailable.");
        return Create(pipeName, currentUser);
    }

    internal static NamedPipeServerStream Create(
        string pipeName,
        SecurityIdentifier currentUser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(currentUser);
        var sid = currentUser.Value;
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm(
            $"O:{sid}G:{sid}D:P(A;;FA;;;{sid})");
        var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security,
            HandleInheritability.None,
            PipeAccessRights.TakeOwnership);
        try
        {
            WindowsNativePromptBrokerPipeIntegrity.ApplyMediumIntegrity(
                pipe.SafePipeHandle);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }
}

internal static class WindowsNativePromptBrokerPipeIntegrity
{
    private const int SeKernelObject = 6;
    private const uint SecurityDescriptorRevision = 1;
    private const uint LabelSecurityInformation = 0x00000010;
    private const string MediumIntegrityNoWriteUpSddl = "S:(ML;;NW;;;ME)";

    public static void ApplyMediumIntegrity(SafePipeHandle pipeHandle)
    {
        ArgumentNullException.ThrowIfNull(pipeHandle);
        if (pipeHandle.IsInvalid || pipeHandle.IsClosed)
        {
            throw new ArgumentException("The named-pipe handle is not usable.", nameof(pipeHandle));
        }

        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                MediumIntegrityNoWriteUpSddl,
                SecurityDescriptorRevision,
                out var securityDescriptor,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Failed to create the native prompt broker integrity label.");
        }

        try
        {
            if (!GetSecurityDescriptorSacl(
                    securityDescriptor,
                    out var saclPresent,
                    out var sacl,
                    out _) ||
                !saclPresent ||
                sacl == nint.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Failed to read the native prompt broker integrity label.");
            }

            var result = SetSecurityInfo(
                pipeHandle,
                SeKernelObject,
                LabelSecurityInformation,
                nint.Zero,
                nint.Zero,
                nint.Zero,
                sacl);
            if (result != 0)
            {
                throw new Win32Exception(
                    checked((int)result),
                    "Failed to apply the native prompt broker integrity label.");
            }
        }
        finally
        {
            _ = LocalFree(securityDescriptor);
        }
    }

    [DllImport(
        "Advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out nint securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(
        nint securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool saclPresent,
        out nint sacl,
        [MarshalAs(UnmanagedType.Bool)] out bool saclDefaulted);

    [DllImport("Advapi32.dll")]
    private static extern uint SetSecurityInfo(
        SafePipeHandle handle,
        int objectType,
        uint securityInformation,
        nint owner,
        nint group,
        nint dacl,
        nint sacl);

    [DllImport("Kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}

internal sealed class WindowsNativePromptBrokerHost
{
    private readonly WindowsNativePromptBrokerServer _server;
    private readonly string _pipeName;
    private readonly INativePromptBrokerClientInspector _clientInspector;
    private readonly INativePromptBrokerHostProtocol _protocol;
    private readonly INativePromptBrokerUpdateFinalizer _updateFinalizer;

    public WindowsNativePromptBrokerHost(
        WindowsNativePromptBrokerServer server,
        string pipeName,
        INativePromptBrokerClientInspector? clientInspector = null,
        INativePromptBrokerHostProtocol? protocol = null,
        INativePromptBrokerUpdateFinalizer? updateFinalizer = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        _clientInspector = clientInspector ?? new WindowsNativePromptBrokerClientInspector();
        _protocol = protocol ?? new NativePromptBrokerHostProtocol();
        _updateFinalizer = updateFinalizer ?? NullNativePromptBrokerUpdateFinalizer.Instance;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe = CreatePipe();
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                await HandleClientAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await _server.StopAsync().ConfigureAwait(false);
        }
    }

    private NamedPipeServerStream CreatePipe() =>
        WindowsNativePromptBrokerPipeFactory.Create(_pipeName);

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            NativePromptArmRequest? request = null;
            NativePromptBrokerArmResult? arm = null;
            try
            {
                var identity = _clientInspector.Inspect(pipe);
                if (identity is null || !_server.IsExpectedClient(identity))
                {
                    return;
                }

                await _protocol.ReadHealthRequestAsync(
                    pipe,
                    cancellationToken).ConfigureAwait(false);
                var health = new NativePromptBrokerHealthResult(
                    _server.IsHealthy,
                    NativePromptProtocol.ProtocolVersion);
                await _protocol.WriteHealthResponseAsync(
                    pipe,
                    health,
                    cancellationToken).ConfigureAwait(false);
                if (health.IsHealthy)
                {
                    try
                    {
                        await _updateFinalizer.FinalizeAsync(
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                try
                {
                    request = await _protocol.ReadArmRequestAsync(
                        pipe,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    return;
                }

                arm = await _server.ArmAsync(
                    identity,
                    request,
                    cancellationToken).ConfigureAwait(false);
                request = null;
                await _protocol.WriteArmResponseAsync(
                    pipe,
                    arm.Response,
                    cancellationToken).ConfigureAwait(false);
                if (arm.Attempt is null)
                {
                    return;
                }

                var completion = await ExecuteUntilDisconnectAsync(
                    arm.Attempt,
                    pipe,
                    cancellationToken).ConfigureAwait(false);
                await _protocol.WriteCompletionAsync(
                    pipe,
                    new(arm.Response.AttemptId, completion.Status, completion.PublicErrorCode),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (InvalidDataException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                request?.Dispose();
                if (arm is not null)
                {
                    await arm.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    internal static async Task<ClientCertificateNativePromptResult> ExecuteUntilDisconnectAsync(
        NativePromptBrokerAttempt attempt,
        Stream connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(connection);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var disconnect = WaitForDisconnectAsync(connection, executionCancellation.Token);
        var execution = attempt.ExecuteAsync(executionCancellation.Token).AsTask();
        var completed = await Task.WhenAny(execution, disconnect).ConfigureAwait(false);
        if (completed == disconnect)
        {
            executionCancellation.Cancel();
            try
            {
                await disconnect.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }

            return await execution.ConfigureAwait(false);
        }

        executionCancellation.Cancel();
        try
        {
            await disconnect.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }

        return await execution.ConfigureAwait(false);
    }

    private static async Task WaitForDisconnectAsync(
        Stream connection,
        CancellationToken cancellationToken)
    {
        var probe = new byte[1];
        try
        {
            _ = await connection.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(probe, 0, probe.Length);
        }
    }
}

internal sealed record NativePromptBrokerPolicyDocument(
    int ProtocolVersion,
    string PolicyHash,
    ClientCertificateNativePromptOptions Prompt,
    NativePromptBrokerTrustedClient? TrustedClient = null);

internal static class NativePromptBrokerPolicyFile
{
    private static readonly HashSet<string> ForbiddenSecretFields = new(
        ["password", "pin", "secret", "credential", "passphrase"],
        StringComparer.OrdinalIgnoreCase);

    public static NativePromptBrokerPolicy Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Native prompt broker policy path is invalid.");
        }

        var json = File.ReadAllBytes(path);
        try
        {
            return Parse(json);
        }
        finally
        {
            Array.Clear(json, 0, json.Length);
        }
    }

    public static NativePromptBrokerPolicy LoadInstalled(string path)
    {
        var policy = Load(path);
        if (!policy.IsValid)
        {
            throw new InvalidDataException("Installed native prompt broker policy is incomplete.");
        }

        return policy;
    }

    public static byte[] BindTrustedClient(
        ReadOnlyMemory<byte> packageJson,
        NativePromptBrokerTrustedClient trustedClient)
    {
        ArgumentNullException.ThrowIfNull(trustedClient);
        if (!trustedClient.IsValid)
        {
            throw new InvalidDataException("Native prompt trusted client identity is invalid.");
        }

        var package = Parse(packageJson);
        if (package.TrustedClient is not null)
        {
            throw new InvalidDataException("Package policy cannot preselect a trusted client.");
        }

        return JsonSerializer.SerializeToUtf8Bytes(new NativePromptBrokerPolicyDocument(
            NativePromptProtocol.ProtocolVersion,
            package.PolicyHash,
            package.Prompt,
            trustedClient));
    }

    public static NativePromptBrokerPolicy Parse(ReadOnlyMemory<byte> json)
    {
        if (json.IsEmpty || json.Length > 64 * 1024)
        {
            throw new InvalidDataException("Native prompt broker policy size is invalid.");
        }

        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        RejectSecretFields(parsed.RootElement);
        NativePromptBrokerPolicyDocument document;
        try
        {
            document = JsonSerializer.Deserialize<NativePromptBrokerPolicyDocument>(
                json.Span,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = false,
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
                }) ?? throw new InvalidDataException("Native prompt broker policy is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Native prompt broker policy JSON is invalid.", exception);
        }
        var policy = new NativePromptBrokerPolicy(
            document.PolicyHash,
            document.Prompt,
            document.TrustedClient);
        if (document.ProtocolVersion != NativePromptProtocol.ProtocolVersion ||
            !policy.IsPackageValid ||
            (document.TrustedClient is not null && !document.TrustedClient.IsValid))
        {
            throw new InvalidDataException("Native prompt broker policy is invalid.");
        }

        return policy;
    }

    private static void RejectSecretFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "Native prompt broker policy contains duplicate or case-ambiguous fields.");
                }

                if (ForbiddenSecretFields.Contains(property.Name))
                {
                    throw new InvalidDataException("Native prompt broker policy contains a forbidden secret field.");
                }

                RejectSecretFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectSecretFields(item);
            }
        }
    }
}

internal static class WindowsNativePromptBrokerRuntime
{
    public static async Task<int> RunAsync(string policyPath, CancellationToken cancellationToken)
    {
        var policy = NativePromptBrokerPolicyFile.LoadInstalled(policyPath);
        var userSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            return 20;
        }

        await using var server = new WindowsNativePromptBrokerServer(
            policy,
            userSid,
            Process.GetCurrentProcess().SessionId,
            new WindowsNativePromptExecutor());
        var host = new WindowsNativePromptBrokerHost(
            server,
            policy.Prompt.BrokerPipeName,
            updateFinalizer: new WindowsNativePromptBrokerUpdateFinalizer());
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
