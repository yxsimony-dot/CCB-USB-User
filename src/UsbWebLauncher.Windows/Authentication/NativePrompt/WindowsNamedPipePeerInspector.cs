using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal enum NativePromptPeerInspectionStatus
{
    Verified = 0,
    MissingServerProcessId = 1,
    PathMismatch = 2,
    IntegrityMismatch = 3,
    InspectionFailed = 4
}

internal interface IWindowsNamedPipePeerNativeApi
{
    bool TryGetServerProcessId(
        INativePromptClientConnection connection,
        out int processId);

    bool TryGetProcessPath(int processId, out string processPath);

    bool TryGetIntegrityRid(int processId, out int integrityRid);
}

internal sealed class WindowsNamedPipePeerInspector : INativePromptPeerInspector
{
    private const string RelativeBrokerPath =
        @"UsbWebLauncher\NativePromptBroker\UsbWebLauncher.NativePromptBroker.exe";
    private readonly IWindowsNamedPipePeerNativeApi _native;

    public const int HighIntegrityRid = 0x00003000;

    public WindowsNamedPipePeerInspector()
        : this(
            new WindowsNamedPipePeerNativeApi(),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
    {
    }

    internal WindowsNamedPipePeerInspector(
        IWindowsNamedPipePeerNativeApi native,
        string programFilesPath)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        var candidate = Path.Combine(
            programFilesPath ?? throw new ArgumentNullException(nameof(programFilesPath)),
            RelativeBrokerPath);
        ExpectedBrokerPath = NormalizeAbsoluteLocalPath(candidate)
            ?? throw new InvalidOperationException("The protected broker installation path is invalid.");
    }

    internal string ExpectedBrokerPath { get; }

    public ValueTask<NativePromptPeerInspectionStatus> InspectAsync(
        INativePromptClientConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_native.TryGetServerProcessId(connection, out var processId) || processId <= 0)
            {
                return ValueTask.FromResult(NativePromptPeerInspectionStatus.MissingServerProcessId);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_native.TryGetProcessPath(processId, out var processPath))
            {
                return ValueTask.FromResult(NativePromptPeerInspectionStatus.PathMismatch);
            }

            var normalizedProcessPath = NormalizeAbsoluteLocalPath(processPath);
            if (normalizedProcessPath is null ||
                !string.Equals(
                    normalizedProcessPath,
                    ExpectedBrokerPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(NativePromptPeerInspectionStatus.PathMismatch);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_native.TryGetIntegrityRid(processId, out var integrityRid))
            {
                return ValueTask.FromResult(NativePromptPeerInspectionStatus.InspectionFailed);
            }

            return ValueTask.FromResult(
                integrityRid == HighIntegrityRid
                    ? NativePromptPeerInspectionStatus.Verified
                    : NativePromptPeerInspectionStatus.IntegrityMismatch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ValueTask.FromResult(NativePromptPeerInspectionStatus.InspectionFailed);
        }
    }

    private static string? NormalizeAbsoluteLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

internal sealed class WindowsNamedPipePeerNativeApi : IWindowsNamedPipePeerNativeApi
{
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint TokenQuery = 0x00000008;
    private const int TokenIntegrityLevel = 25;
    private const int MaximumPathCharacters = 32_768;

    public bool TryGetServerProcessId(
        INativePromptClientConnection connection,
        out int processId)
    {
        processId = 0;
        if (connection is not WindowsNamedPipeClientConnection windowsConnection ||
            !GetNamedPipeServerProcessId(
                windowsConnection.Pipe.SafePipeHandle,
                out var nativeProcessId) ||
            nativeProcessId is 0 or > int.MaxValue)
        {
            return false;
        }

        processId = checked((int)nativeProcessId);
        return true;
    }

    public bool TryGetProcessPath(int processId, out string processPath)
    {
        processPath = string.Empty;
        using var process = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)processId));
        if (process.IsInvalid)
        {
            return false;
        }

        var path = new StringBuilder(MaximumPathCharacters);
        var length = checked((uint)path.Capacity);
        if (!QueryFullProcessImageName(process, 0, path, ref length) || length == 0)
        {
            return false;
        }

        processPath = path.ToString(0, checked((int)length));
        return true;
    }

    public bool TryGetIntegrityRid(int processId, out int integrityRid)
    {
        integrityRid = 0;
        using var process = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)processId));
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
        {
            return false;
        }

        using (token)
        {
            _ = GetTokenInformation(
                token,
                TokenIntegrityLevel,
                IntPtr.Zero,
                0,
                out var requiredLength);
            if (requiredLength <= 0)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal(requiredLength);
            try
            {
                if (!GetTokenInformation(
                        token,
                        TokenIntegrityLevel,
                        buffer,
                        requiredLength,
                        out _))
                {
                    return false;
                }

                var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                if (label.Label.Sid == IntPtr.Zero)
                {
                    return false;
                }

                var countPointer = GetSidSubAuthorityCount(label.Label.Sid);
                if (countPointer == IntPtr.Zero)
                {
                    return false;
                }

                var count = Marshal.ReadByte(countPointer);
                if (count == 0)
                {
                    return false;
                }

                var ridPointer = GetSidSubAuthority(label.Label.Sid, checked((uint)(count - 1)));
                if (ridPointer == IntPtr.Zero)
                {
                    return false;
                }

                integrityRid = Marshal.ReadInt32(ridPointer);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

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

    [DllImport("Advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        IntPtr information,
        int informationLength,
        out int returnLength);

    [DllImport("Advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("Advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }
}
