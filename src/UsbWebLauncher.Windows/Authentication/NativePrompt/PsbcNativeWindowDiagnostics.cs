using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal enum PsbcNativeWindowAuthenticodeStatus
{
    Valid,
    InvalidOrUnsigned,
    Unavailable
}

internal sealed record PsbcNativeWindowProcessSnapshot(
    int ProcessId,
    int ParentProcessId,
    string ProcessPath,
    string ProcessSha256,
    PsbcNativeWindowAuthenticodeStatus AuthenticodeStatus,
    string SignerCertificateSubject,
    string SignerCertificateThumbprint,
    int? IntegrityRid,
    int? WindowsSessionId,
    int CurrentWindowsSessionId);

internal sealed record PsbcNativeWindowNodeSnapshot(
    string WindowHandle,
    string ParentWindowHandle,
    string OwnerWindowHandle,
    string RootWindowHandle,
    int HierarchyDepth,
    bool IsTopLevel,
    bool IsVisible,
    bool IsEnabled,
    int ProcessId,
    string WindowClass,
    int ControlId,
    bool IsPasswordControl,
    bool IsDefaultButton,
    string WindowTitle);

internal sealed record PsbcNativeWindowCandidateSnapshot(
    PsbcNativeWindowProcessSnapshot Process,
    PsbcNativeWindowNodeSnapshot RootWindow,
    IReadOnlyList<PsbcNativeWindowNodeSnapshot> Descendants);

internal interface IPsbcNativeWindowSnapshotSource
{
    IReadOnlyList<PsbcNativeWindowCandidateSnapshot> CapturePasswordPromptCandidates();
}

public sealed class PsbcNativeWindowDiagnostics
{
    private const long MaximumFileBytes = 1024 * 1024;
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);
    private readonly object sync = new();
    private readonly string path;
    private readonly IPsbcNativeWindowSnapshotSource source;
    private readonly TimeSpan pollInterval;
    private readonly JsonSerializerOptions serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private string? lastSnapshotHash;

    public PsbcNativeWindowDiagnostics(string path)
        : this(path, new WindowsPsbcNativeWindowSnapshotSource(), DefaultPollInterval)
    {
    }

    internal PsbcNativeWindowDiagnostics(
        string path,
        IPsbcNativeWindowSnapshotSource source,
        TimeSpan pollInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "PSBC native window diagnostics require an absolute output path.",
                nameof(path));
        }

        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        this.path = Path.GetFullPath(path);
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.pollInterval = pollInterval;
    }

    internal string PathName => path;

    public void BeginRun()
    {
        lock (sync)
        {
            lastSnapshotHash = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }
            catch
            {
                // Diagnostics must not change authentication or UI behavior.
            }
        }
    }

    public void DeleteOutput()
    {
        lock (sync)
        {
            lastSnapshotHash = null;
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Diagnostics cleanup must not change authentication or UI behavior.
            }
        }
    }

    public async Task ObserveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureOnce();
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal void CaptureOnce()
    {
        IReadOnlyList<PsbcNativeWindowCandidateSnapshot> candidates;
        try
        {
            candidates = source.CapturePasswordPromptCandidates();
        }
        catch
        {
            return;
        }

        var stableSnapshot = JsonSerializer.Serialize(candidates, serializerOptions);
        var snapshotHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(stableSnapshot)));

        lock (sync)
        {
            if (string.Equals(lastSnapshotHash, snapshotHash, StringComparison.Ordinal))
            {
                return;
            }

            lastSnapshotHash = snapshotHash;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumFileBytes)
                {
                    return;
                }

                var payload = new
                {
                    schemaVersion = 1,
                    capturedAtUtc = DateTimeOffset.UtcNow,
                    containsWindowIdentityData = true,
                    secretDataIncluded = false,
                    passwordControlTextRead = false,
                    inputCharacterCountRead = false,
                    activeOperationsPerformed = false,
                    candidates
                };
                File.AppendAllText(
                    path,
                    JsonSerializer.Serialize(payload, serializerOptions) +
                    Environment.NewLine);
            }
            catch
            {
                // Diagnostics must not change authentication or UI behavior.
            }
        }
    }
}

internal sealed class WindowsPsbcNativeWindowSnapshotSource
    : IPsbcNativeWindowSnapshotSource
{
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const int ProcessBasicInformationClass = 0;
    private const uint TokenQuery = 0x00000008;
    private const int TokenIntegrityLevel = 25;
    private const uint GetAncestorRoot = 2;
    private const uint GetWindowOwner = 4;
    private const int MaximumPathCharacters = 32_768;
    private const int MaximumCandidates = 32;
    private const int MaximumDescendants = 256;
    private const int MaximumHierarchyDepth = 32;
    private const long EditPasswordStyle = 0x20;
    private const long ButtonTypeMask = 0x0F;
    private const long DefaultPushButtonStyle = 0x01;
    private const uint WinTrustUiNone = 2;
    private const uint WinTrustRevokeNone = 0;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustStateActionVerify = 1;
    private const uint WinTrustStateActionClose = 2;
    private const uint WinTrustCacheOnlyUrlRetrieval = 0x00001000;
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public IReadOnlyList<PsbcNativeWindowCandidateSnapshot>
        CapturePasswordPromptCandidates()
    {
        var candidates = new List<PsbcNativeWindowCandidateSnapshot>();
        EnumWindowsCallback callback = (windowHandle, parameter) =>
        {
            _ = parameter;
            if (candidates.Count >= MaximumCandidates)
            {
                return false;
            }

            var candidate = CaptureWindowHierarchy(windowHandle);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }

            return true;
        };

        if (!EnumWindows(callback, IntPtr.Zero))
        {
            return candidates;
        }

        GC.KeepAlive(callback);
        return candidates;
    }

    internal PsbcNativeWindowCandidateSnapshot? CaptureWindowHierarchy(
        IntPtr rootWindow)
    {
        try
        {
            if (rootWindow == IntPtr.Zero ||
                GetAncestor(rootWindow, GetAncestorRoot) != rootWindow ||
                !IsWindowVisible(rootWindow) ||
                !TryGetWindowClass(rootWindow, out var rootClass) ||
                !TryGetWindowTitle(rootWindow, out var rootTitle))
            {
                return null;
            }

            GetWindowThreadProcessId(rootWindow, out var nativeProcessId);
            if (nativeProcessId is 0 or > int.MaxValue)
            {
                return null;
            }

            var processId = checked((int)nativeProcessId);
            var descendants = CaptureDescendants(rootWindow);
            if (!descendants.Any(node => node.IsPasswordControl))
            {
                return null;
            }

            var rootNode = new PsbcNativeWindowNodeSnapshot(
                FormatHandle(rootWindow),
                FormatHandle(GetParent(rootWindow)),
                FormatHandle(GetWindow(rootWindow, GetWindowOwner)),
                FormatHandle(rootWindow),
                HierarchyDepth: 0,
                IsTopLevel: true,
                IsVisible: true,
                IsEnabled: IsWindowEnabled(rootWindow),
                ProcessId: processId,
                WindowClass: rootClass,
                ControlId: GetDlgCtrlID(rootWindow),
                IsPasswordControl: false,
                IsDefaultButton: false,
                WindowTitle: rootTitle);

            return new(
                CaptureProcess(processId, nativeProcessId),
                rootNode,
                descendants);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<PsbcNativeWindowNodeSnapshot> CaptureDescendants(
        IntPtr rootWindow)
    {
        var descendants = new List<PsbcNativeWindowNodeSnapshot>();
        EnumWindowsCallback callback = (windowHandle, parameter) =>
        {
            _ = parameter;
            if (descendants.Count >= MaximumDescendants ||
                !TryGetWindowClass(windowHandle, out var windowClass))
            {
                return descendants.Count < MaximumDescendants;
            }

            GetWindowThreadProcessId(windowHandle, out var nativeProcessId);
            if (nativeProcessId > int.MaxValue)
            {
                return true;
            }

            var style = GetWindowStyle(windowHandle);
            descendants.Add(new(
                FormatHandle(windowHandle),
                FormatHandle(GetParent(windowHandle)),
                FormatHandle(GetWindow(windowHandle, GetWindowOwner)),
                FormatHandle(GetAncestor(windowHandle, GetAncestorRoot)),
                GetHierarchyDepth(rootWindow, windowHandle),
                IsTopLevel: false,
                IsVisible: IsWindowVisible(windowHandle),
                IsEnabled: IsWindowEnabled(windowHandle),
                ProcessId: checked((int)nativeProcessId),
                WindowClass: windowClass,
                ControlId: GetDlgCtrlID(windowHandle),
                IsPasswordControl:
                    string.Equals(windowClass, "Edit", StringComparison.Ordinal) &&
                    (style & EditPasswordStyle) != 0,
                IsDefaultButton:
                    string.Equals(windowClass, "Button", StringComparison.Ordinal) &&
                    (style & ButtonTypeMask) == DefaultPushButtonStyle,
                WindowTitle: string.Empty));
            return true;
        };

        _ = EnumChildWindows(rootWindow, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return descendants;
    }

    private static PsbcNativeWindowProcessSnapshot CaptureProcess(
        int processId,
        uint nativeProcessId)
    {
        var parentProcessId = 0;
        var processPath = string.Empty;
        var processSha256 = string.Empty;
        var authenticodeStatus = PsbcNativeWindowAuthenticodeStatus.Unavailable;
        var signerSubject = string.Empty;
        var signerThumbprint = string.Empty;
        int? integrityRid = null;
        int? sessionId = null;
        var currentSessionId = TryGetSessionId(
            checked((uint)Environment.ProcessId),
            out var observedCurrentSessionId)
            ? observedCurrentSessionId
            : -1;

        using var process = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            nativeProcessId);
        if (!process.IsInvalid)
        {
            _ = TryGetParentProcessId(process, out parentProcessId);
            if (TryGetProcessPath(process, out processPath))
            {
                _ = TryComputeSha256(processPath, out processSha256);
                authenticodeStatus = ReadAuthenticode(
                    processPath,
                    out signerSubject,
                    out signerThumbprint);
            }

            if (TryGetProcessIntegrityRid(process, out var observedIntegrityRid))
            {
                integrityRid = observedIntegrityRid;
            }
        }

        if (TryGetSessionId(nativeProcessId, out var observedSessionId))
        {
            sessionId = observedSessionId;
        }

        return new(
            processId,
            parentProcessId,
            processPath,
            processSha256,
            authenticodeStatus,
            signerSubject,
            signerThumbprint,
            integrityRid,
            sessionId,
            currentSessionId);
    }

    private static int GetHierarchyDepth(IntPtr rootWindow, IntPtr windowHandle)
    {
        var depth = 0;
        var current = windowHandle;
        while (current != IntPtr.Zero &&
               current != rootWindow &&
               depth < MaximumHierarchyDepth)
        {
            current = GetParent(current);
            depth++;
        }

        return current == rootWindow ? depth : -1;
    }

    private static bool TryGetWindowTitle(IntPtr windowHandle, out string title)
    {
        title = string.Empty;
        var length = GetWindowTextLength(windowHandle);
        if (length == 0)
        {
            return true;
        }

        var buffer = new StringBuilder(checked(length + 1));
        var copied = GetWindowText(windowHandle, buffer, buffer.Capacity);
        if (copied != length)
        {
            title = string.Empty;
            return false;
        }

        title = buffer.ToString();
        return true;
    }

    private static bool TryGetWindowClass(
        IntPtr windowHandle,
        out string windowClass)
    {
        var buffer = new StringBuilder(256);
        var copied = GetClassName(windowHandle, buffer, buffer.Capacity);
        windowClass = copied > 0 ? buffer.ToString() : string.Empty;
        return copied > 0;
    }

    private static bool TryGetProcessPath(
        SafeProcessHandle process,
        out string processPath)
    {
        var buffer = new StringBuilder(MaximumPathCharacters);
        var length = checked((uint)buffer.Capacity);
        if (!QueryFullProcessImageName(process, 0, buffer, ref length) ||
            length == 0)
        {
            processPath = string.Empty;
            return false;
        }

        processPath = buffer.ToString(0, checked((int)length));
        return Path.IsPathFullyQualified(processPath) &&
               !processPath.StartsWith(@"\\", StringComparison.Ordinal) &&
               !processPath.StartsWith("//", StringComparison.Ordinal);
    }

    private static bool TryGetParentProcessId(
        SafeProcessHandle process,
        out int parentProcessId)
    {
        parentProcessId = 0;
        var informationSize = Marshal.SizeOf<ProcessBasicInformation>();
        var status = NtQueryInformationProcess(
            process,
            ProcessBasicInformationClass,
            out var information,
            informationSize,
            out var returnLength);
        if (status != 0 || returnLength < informationSize)
        {
            return false;
        }

        var nativeParentProcessId = information.InheritedFromUniqueProcessId.ToUInt64();
        if (nativeParentProcessId is 0 or > int.MaxValue)
        {
            return false;
        }

        parentProcessId = checked((int)nativeParentProcessId);
        return true;
    }

    private static bool TryGetProcessIntegrityRid(
        SafeProcessHandle process,
        out int integrityRid)
    {
        integrityRid = 0;
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return false;
        }

        using (token)
        {
            if (!TryGetTokenInformationBuffer(
                    token,
                    TokenIntegrityLevel,
                    out var buffer))
            {
                return false;
            }

            try
            {
                var label = Marshal.PtrToStructure<TokenMandatoryLabelRecord>(buffer);
                if (label.Label.Sid == IntPtr.Zero || !IsValidSid(label.Label.Sid))
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

                var ridPointer = GetSidSubAuthority(
                    label.Label.Sid,
                    checked((uint)(count - 1)));
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

    private static bool TryGetTokenInformationBuffer(
        SafeAccessTokenHandle token,
        int informationClass,
        out IntPtr buffer)
    {
        buffer = IntPtr.Zero;
        _ = GetTokenInformation(
            token,
            informationClass,
            IntPtr.Zero,
            0,
            out var requiredLength);
        if (requiredLength <= 0)
        {
            return false;
        }

        buffer = Marshal.AllocHGlobal(requiredLength);
        if (GetTokenInformation(
                token,
                informationClass,
                buffer,
                requiredLength,
                out _))
        {
            return true;
        }

        Marshal.FreeHGlobal(buffer);
        buffer = IntPtr.Zero;
        return false;
    }

    private static bool TryGetSessionId(uint processId, out int sessionId)
    {
        sessionId = -1;
        if (!ProcessIdToSessionId(processId, out var nativeSessionId) ||
            nativeSessionId > int.MaxValue)
        {
            return false;
        }

        sessionId = checked((int)nativeSessionId);
        return true;
    }

    private static bool TryComputeSha256(string processPath, out string sha256)
    {
        sha256 = string.Empty;
        try
        {
            using var stream = new FileStream(
                processPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            sha256 = Convert.ToHexString(SHA256.HashData(stream));
            return sha256.Length == 64;
        }
        catch
        {
            sha256 = string.Empty;
            return false;
        }
    }

    private static PsbcNativeWindowAuthenticodeStatus ReadAuthenticode(
        string processPath,
        out string signerSubject,
        out string signerThumbprint)
    {
        signerSubject = string.Empty;
        signerThumbprint = string.Empty;
        IntPtr path = IntPtr.Zero;
        IntPtr fileInfoBuffer = IntPtr.Zero;
        var trustData = new WinTrustData();
        var verificationAttempted = false;
        try
        {
            path = Marshal.StringToCoTaskMemUni(processPath);
            var fileInfo = new WinTrustFileInfo
            {
                Size = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = path
            };
            fileInfoBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoBuffer, fDeleteOld: false);
            trustData = new WinTrustData
            {
                Size = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = WinTrustUiNone,
                RevocationChecks = WinTrustRevokeNone,
                UnionChoice = WinTrustChoiceFile,
                FileInfo = fileInfoBuffer,
                StateAction = WinTrustStateActionVerify,
                ProviderFlags = WinTrustCacheOnlyUrlRetrieval
            };

            var action = WinTrustActionGenericVerifyV2;
            verificationAttempted = true;
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref trustData) != 0)
            {
                return PsbcNativeWindowAuthenticodeStatus.InvalidOrUnsigned;
            }

            if (trustData.StateData == IntPtr.Zero)
            {
                return PsbcNativeWindowAuthenticodeStatus.Unavailable;
            }

            var providerData = WTHelperProvDataFromStateData(trustData.StateData);
            if (providerData == IntPtr.Zero)
            {
                return PsbcNativeWindowAuthenticodeStatus.Unavailable;
            }

            var providerSigner = WTHelperGetProvSignerFromChain(
                providerData,
                signerIndex: 0,
                counterSigner: false,
                counterSignerIndex: 0);
            if (providerSigner == IntPtr.Zero)
            {
                return PsbcNativeWindowAuthenticodeStatus.Unavailable;
            }

            var providerCertificate = WTHelperGetProvCertFromChain(
                providerSigner,
                certificateIndex: 0);
            if (providerCertificate == IntPtr.Zero)
            {
                return PsbcNativeWindowAuthenticodeStatus.Unavailable;
            }

            var leaf = Marshal.PtrToStructure<CryptProviderCertificateHeader>(
                providerCertificate);
            if (leaf.CertificateContext == IntPtr.Zero)
            {
                return PsbcNativeWindowAuthenticodeStatus.Unavailable;
            }

            using var certificate = new X509Certificate2(leaf.CertificateContext);
            signerSubject = certificate.Subject;
            signerThumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA1);
            return signerThumbprint.Length == 40
                ? PsbcNativeWindowAuthenticodeStatus.Valid
                : PsbcNativeWindowAuthenticodeStatus.Unavailable;
        }
        catch
        {
            signerSubject = string.Empty;
            signerThumbprint = string.Empty;
            return PsbcNativeWindowAuthenticodeStatus.Unavailable;
        }
        finally
        {
            if (verificationAttempted)
            {
                try
                {
                    trustData.StateAction = WinTrustStateActionClose;
                    var action = WinTrustActionGenericVerifyV2;
                    _ = WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
                }
                catch
                {
                }
            }

            if (fileInfoBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoBuffer);
            }

            if (path != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
    }

    private static string FormatHandle(IntPtr handle) =>
        string.Concat("0x", handle.ToInt64().ToString("X"));

    private static long GetWindowStyle(IntPtr window) => IntPtr.Size == 8
        ? GetWindowLongPtrW(window, -16).ToInt64()
        : GetWindowLongW(window, -16);

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        IntPtr parentWindow,
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("User32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("User32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr windowHandle);

    [DllImport("User32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("User32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("User32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(
        IntPtr windowHandle,
        StringBuilder text,
        int maximumCount);

    [DllImport("User32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(
        IntPtr windowHandle,
        StringBuilder className,
        int maximumCount);

    [DllImport("User32.dll")]
    private static extern IntPtr GetWindow(IntPtr windowHandle, uint command);

    [DllImport("User32.dll")]
    private static extern IntPtr GetParent(IntPtr windowHandle);

    [DllImport("User32.dll")]
    private static extern int GetDlgCtrlID(IntPtr windowHandle);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport("User32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("User32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr window, int index);

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("Ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle process,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder processPath,
        ref uint size);

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(
        uint processId,
        out uint sessionId);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("Advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("Advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(
        IntPtr sid,
        uint subAuthority);

    [DllImport("Wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [DllImport("Wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("Wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [DllImport("Wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(
        IntPtr providerSigner,
        uint certificateIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributesRecord
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabelRecord
    {
        public SidAndAttributesRecord Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public int ExitStatus;
        public IntPtr PebBaseAddress;
        public UIntPtr AffinityMask;
        public int BasePriority;
        public UIntPtr UniqueProcessId;
        public UIntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificateHeader
    {
        public uint Size;
        public IntPtr CertificateContext;
    }
}
