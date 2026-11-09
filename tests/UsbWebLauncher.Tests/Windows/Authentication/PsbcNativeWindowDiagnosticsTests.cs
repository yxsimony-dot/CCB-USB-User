using System.Runtime.InteropServices;
using System.Text.Json;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class PsbcNativeWindowDiagnosticsTests
{
    private const uint WindowStyleVisible = 0x10000000;
    private const uint WindowStyleChild = 0x40000000;
    private const uint WindowStyleTabStop = 0x00010000;
    private const uint EditPasswordStyle = 0x20;
    private const uint ButtonDefaultPushStyle = 0x01;
    private const int WindowMessageGetText = 0x000D;
    private const int WindowMessageGetTextLength = 0x000E;
    private const int ButtonMessageClick = 0x00F5;
    private const int WindowLongPointerWindowProcedure = -4;
    private const string SyntheticPasswordText =
        "SYNTHETIC_PASSWORD_CONTROL_SECRET";
    private const string SyntheticButtonText =
        "SYNTHETIC_CONFIRM_CONTROL_SECRET";

    [Fact]
    public void Capture_TestWindowReadsStructureWithoutPasswordTextCountFocusOrClick()
    {
        var dialog = CreateWindowExW(
            0x08000000,
            "#32770",
            "SYNTHETIC PSBC UK PASSWORD WINDOW",
            WindowStyleVisible,
            -32000,
            -32000,
            240,
            120,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, dialog);

        var password = CreateWindowExW(
            0,
            "Edit",
            SyntheticPasswordText,
            WindowStyleVisible | WindowStyleChild | WindowStyleTabStop |
                EditPasswordStyle,
            10,
            10,
            160,
            24,
            dialog,
            new IntPtr(1001),
            IntPtr.Zero,
            IntPtr.Zero);
        var confirm = CreateWindowExW(
            0,
            "Button",
            SyntheticButtonText,
            WindowStyleVisible | WindowStyleChild | WindowStyleTabStop |
                ButtonDefaultPushStyle,
            10,
            45,
            80,
            24,
            dialog,
            new IntPtr(1),
            IntPtr.Zero,
            IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, password);
        Assert.NotEqual(IntPtr.Zero, confirm);

        var passwordTextReads = 0;
        var passwordLengthReads = 0;
        var confirmClicks = 0;
        WindowProcedure? passwordProcedure = null;
        WindowProcedure? confirmProcedure = null;
        var originalPasswordProcedure = IntPtr.Zero;
        var originalConfirmProcedure = IntPtr.Zero;
        try
        {
            passwordProcedure = (window, message, wordParameter, longParameter) =>
            {
                if (message == WindowMessageGetText)
                {
                    Interlocked.Increment(ref passwordTextReads);
                }
                else if (message == WindowMessageGetTextLength)
                {
                    Interlocked.Increment(ref passwordLengthReads);
                }

                return CallWindowProcW(
                    originalPasswordProcedure,
                    window,
                    message,
                    wordParameter,
                    longParameter);
            };
            confirmProcedure = (window, message, wordParameter, longParameter) =>
            {
                if (message == ButtonMessageClick)
                {
                    Interlocked.Increment(ref confirmClicks);
                }

                return CallWindowProcW(
                    originalConfirmProcedure,
                    window,
                    message,
                    wordParameter,
                    longParameter);
            };
            originalPasswordProcedure = SetWindowLongPointer(
                password,
                WindowLongPointerWindowProcedure,
                Marshal.GetFunctionPointerForDelegate(passwordProcedure));
            originalConfirmProcedure = SetWindowLongPointer(
                confirm,
                WindowLongPointerWindowProcedure,
                Marshal.GetFunctionPointerForDelegate(confirmProcedure));
            Assert.NotEqual(IntPtr.Zero, originalPasswordProcedure);
            Assert.NotEqual(IntPtr.Zero, originalConfirmProcedure);

            var foregroundBefore = GetForegroundWindow();
            var focusBefore = GetFocus();
            var snapshot = new WindowsPsbcNativeWindowSnapshotSource()
                .CaptureWindowHierarchy(dialog);

            Assert.NotNull(snapshot);
            Assert.Equal(Environment.ProcessId, snapshot.Process.ProcessId);
            Assert.Equal(
                "SYNTHETIC PSBC UK PASSWORD WINDOW",
                snapshot.RootWindow.WindowTitle);
            Assert.Equal("#32770", snapshot.RootWindow.WindowClass);
            Assert.Equal(foregroundBefore, GetForegroundWindow());
            Assert.Equal(focusBefore, GetFocus());
            Assert.Equal(0, Volatile.Read(ref passwordTextReads));
            Assert.Equal(0, Volatile.Read(ref passwordLengthReads));
            Assert.Equal(0, Volatile.Read(ref confirmClicks));

            var passwordSnapshot = Assert.Single(
                snapshot.Descendants.Where(node => node.ControlId == 1001));
            Assert.Equal("Edit", passwordSnapshot.WindowClass);
            Assert.True(passwordSnapshot.IsPasswordControl);
            Assert.False(passwordSnapshot.IsDefaultButton);
            Assert.Equal(string.Empty, passwordSnapshot.WindowTitle);

            var confirmSnapshot = Assert.Single(
                snapshot.Descendants.Where(node => node.ControlId == 1));
            Assert.Equal("Button", confirmSnapshot.WindowClass);
            Assert.False(confirmSnapshot.IsPasswordControl);
            Assert.True(confirmSnapshot.IsDefaultButton);
            Assert.Equal(string.Empty, confirmSnapshot.WindowTitle);
        }
        finally
        {
            if (originalPasswordProcedure != IntPtr.Zero)
            {
                _ = SetWindowLongPointer(
                    password,
                    WindowLongPointerWindowProcedure,
                    originalPasswordProcedure);
            }

            if (originalConfirmProcedure != IntPtr.Zero)
            {
                _ = SetWindowLongPointer(
                    confirm,
                    WindowLongPointerWindowProcedure,
                    originalConfirmProcedure);
            }

            GC.KeepAlive(passwordProcedure);
            GC.KeepAlive(confirmProcedure);
            DestroyWindow(dialog);
        }
    }

    [Fact]
    public void CaptureOnce_WritesPublicStructureWithoutSyntheticSecrets()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "UsbWebLauncherPsbcNativeWindowDiagnosticsTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "native-windows.jsonl");
        try
        {
            var source = new StubSnapshotSource(
            [
                new(
                    new(
                        ProcessId: 100,
                        ParentProcessId: 90,
                        ProcessPath: @"C:\Synthetic\PsbcPrompt.exe",
                        ProcessSha256: new string('A', 64),
                        AuthenticodeStatus:
                            PsbcNativeWindowAuthenticodeStatus.Valid,
                        SignerCertificateSubject: "CN=SYNTHETIC PSBC SIGNER",
                        SignerCertificateThumbprint: new string('B', 40),
                        IntegrityRid: 0x3000,
                        WindowsSessionId: 3,
                        CurrentWindowsSessionId: 3),
                    new(
                        "0x100",
                        "0x0",
                        "0x0",
                        "0x100",
                        0,
                        true,
                        true,
                        true,
                        100,
                        "#32770",
                        0,
                        false,
                        false,
                        "SYNTHETIC PSBC UK PASSWORD WINDOW"),
                    [
                        new(
                            "0x101",
                            "0x100",
                            "0x0",
                            "0x100",
                            1,
                            false,
                            true,
                            true,
                            100,
                            "Edit",
                            1001,
                            true,
                            false,
                            string.Empty),
                        new(
                            "0x102",
                            "0x100",
                            "0x0",
                            "0x100",
                            1,
                            false,
                            true,
                            true,
                            100,
                            "Button",
                            1,
                            false,
                            true,
                            string.Empty)
                    ])
            ]);
            var diagnostics = new PsbcNativeWindowDiagnostics(
                path,
                source,
                TimeSpan.FromMilliseconds(10));
            diagnostics.BeginRun();

            diagnostics.CaptureOnce();

            var output = File.ReadAllText(path);
            using var document = JsonDocument.Parse(output);
            var payload = document.RootElement;
            Assert.Equal(1, payload.GetProperty("schemaVersion").GetInt32());
            Assert.True(
                payload.GetProperty("containsWindowIdentityData").GetBoolean());
            Assert.False(payload.GetProperty("secretDataIncluded").GetBoolean());
            Assert.False(
                payload.GetProperty("passwordControlTextRead").GetBoolean());
            Assert.False(
                payload.GetProperty("inputCharacterCountRead").GetBoolean());
            Assert.False(
                payload.GetProperty("activeOperationsPerformed").GetBoolean());
            var candidate = Assert.Single(
                payload.GetProperty("candidates").EnumerateArray());
            Assert.Equal(
                @"C:\Synthetic\PsbcPrompt.exe",
                candidate.GetProperty("process")
                    .GetProperty("processPath")
                    .GetString());
            var descendants = candidate.GetProperty("descendants");
            Assert.True(descendants[0].GetProperty("isPasswordControl").GetBoolean());
            Assert.True(descendants[1].GetProperty("isDefaultButton").GetBoolean());

            Assert.DoesNotContain(
                SyntheticPasswordText,
                output,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                SyntheticButtonText,
                output,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "credentialManager",
                output,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Constructor_RejectsRelativeOutputPath()
    {
        Assert.Throws<ArgumentException>(() =>
            new PsbcNativeWindowDiagnostics("native-windows.jsonl"));
    }

    [Fact]
    public void BeginRunAndDeleteOutput_ControlFileLifecycleWithoutPropagatingIoFailures()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "UsbWebLauncherPsbcNativeWindowDiagnosticsLifecycleTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "native-windows.jsonl");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(path, "stale diagnostic identity");
            var diagnostics = new PsbcNativeWindowDiagnostics(
                path,
                new StubSnapshotSource([]),
                TimeSpan.FromMilliseconds(10));

            diagnostics.BeginRun();

            Assert.Equal(string.Empty, File.ReadAllText(path));
            File.WriteAllText(path, "synthetic diagnostic identity");

            diagnostics.DeleteOutput();

            Assert.False(File.Exists(path));

            File.WriteAllText(path, "locked synthetic diagnostic identity");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var exception = Record.Exception(diagnostics.DeleteOutput);

                Assert.Null(exception);
                Assert.True(File.Exists(path));
            }

            diagnostics.DeleteOutput();
            Assert.False(File.Exists(path));
            Assert.Null(Record.Exception(diagnostics.DeleteOutput));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class StubSnapshotSource(
        IReadOnlyList<PsbcNativeWindowCandidateSnapshot> snapshots)
        : IPsbcNativeWindowSnapshotSource
    {
        public IReadOnlyList<PsbcNativeWindowCandidateSnapshot>
            CapturePasswordPromptCandidates() => snapshots;
    }

    private delegate IntPtr WindowProcedure(
        IntPtr window,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter);

    private static IntPtr SetWindowLongPointer(
        IntPtr window,
        int index,
        IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtrW(window, index, value)
            : new IntPtr(SetWindowLongW(window, index, value.ToInt32()));

    [DllImport("User32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("User32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("User32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(
        IntPtr window,
        int index,
        IntPtr value);

    [DllImport("User32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLongW(
        IntPtr window,
        int index,
        int value);

    [DllImport("User32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProcW(
        IntPtr previousWindowProcedure,
        IntPtr window,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter);

    [DllImport("User32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("User32.dll")]
    private static extern IntPtr GetFocus();
}
