using System.Runtime.InteropServices;
using System.Diagnostics;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication.NativePrompt;
using Xunit.Abstractions;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class NativePromptLayoutCaptureTests
{
    private readonly ITestOutputHelper output;

    public NativePromptLayoutCaptureTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData("网银盾验证密码", true, 1, false, true)]
    [InlineData("网银盾验证密码", true, 1, true, true)]
    [InlineData("网银盾验证密码", true, 2, false, false)]
    [InlineData("网银盾验证密码", false, 1, false, false)]
    [InlineData("", true, 1, false, false)]
    [InlineData("保存文件", true, 1, false, false)]
    public void Capture_PasswordEditWithoutVisibleButtons_RequiresTitledUniquePassword(
        string title, bool password, int inputCount, bool hiddenKeyboardButton, bool expected)
    {
        var dialog = CreateWindowExW(0x08000000, "#32770", title, 0x10000000,
            -32000, -32000, 200, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, dialog);
        try
        {
            for (var index = 0; index < inputCount; index++)
                Assert.NotEqual(IntPtr.Zero, CreateWindowExW(0, "Edit", "", 0x50000000u | (password ? 0x20u : 0u),
                    0, index * 25, 80, 20, dialog, new IntPtr(1000 + index), IntPtr.Zero, IntPtr.Zero));
            if (hiddenKeyboardButton)
                Assert.NotEqual(IntPtr.Zero, CreateWindowExW(0, "Button", "软键盘", 0x40000000,
                    0, 50, 80, 20, dialog, new IntPtr(2000), IntPtr.Zero, IntPtr.Zero));
            var policy = NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy() with
            { ProcessFileName = Path.GetFileName(Environment.ProcessPath) };
            var snapshot = new WindowsNativePromptApi().CaptureWindowSnapshot(dialog, policy, Environment.ProcessId);
            if (expected)
            {
                Assert.NotNull(snapshot);
                Assert.True(snapshot.UsesDialogEnterSubmit);
                Assert.Equal(
                    NativePromptDialogEnterLayout.TitledPasswordEdit,
                    snapshot.DialogEnterLayout);
                Assert.Equal(IntPtr.Zero, snapshot.SubmitControlHandle);
                Assert.Equal(0, snapshot.SubmitControlId);
                AssertCapturedLayoutMatch(policy, snapshot, title, expected: true);
                Assert.False(new WindowsNativePromptApi().IsSubmitControlReady(snapshot));
            }
            else Assert.Null(snapshot);
        }
        finally { DestroyWindow(dialog); }
    }

    [Theory]
    [InlineData("确认", true)]
    [InlineData("软键盘", false)]
    public void Capture_VisibleButtonUsesStandardSubmitOrPreventsEnterFallback(string caption, bool expected)
    {
        var dialog = CreateWindowExW(0x08000000, "#32770", "网银盾验证密码", 0x10000000,
            -32000, -32000, 200, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, dialog);
        try
        {
            Assert.NotEqual(IntPtr.Zero, CreateWindowExW(0, "Edit", "", 0x50000020,
                0, 0, 80, 20, dialog, new IntPtr(10005), IntPtr.Zero, IntPtr.Zero));
            Assert.NotEqual(IntPtr.Zero, CreateWindowExW(0, "Button", caption, 0x50000000,
                0, 25, 80, 20, dialog, new IntPtr(1), IntPtr.Zero, IntPtr.Zero));
            var policy = NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy() with
            { ProcessFileName = Path.GetFileName(Environment.ProcessPath) };
            var snapshot = new WindowsNativePromptApi().CaptureWindowSnapshot(dialog, policy, Environment.ProcessId);
            if (expected)
            {
                Assert.NotNull(snapshot);
                Assert.False(snapshot.UsesDialogEnterSubmit);
                Assert.NotEqual(IntPtr.Zero, snapshot.SubmitControlHandle);
                AssertCapturedLayoutMatch(policy, snapshot, "网银盾验证密码", expected: true);
            }
            else Assert.Null(snapshot);
        }
        finally { DestroyWindow(dialog); }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1024, false)]
    public void Capture_CaptionlessCcbKeyboardRequiresCompleteFingerprint(
        int omittedControlId,
        bool expected)
    {
        var dialog = CreateWindowExW(0x08000000, "#32770", "", 0x10000000,
            -32000, -32000, 375, 163, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, dialog);
        try
        {
            CreateCaptionlessCcbKeyboard(dialog, omittedControlId);
            var policy = NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy() with
            { ProcessFileName = Path.GetFileName(Environment.ProcessPath) };
            var snapshot = new WindowsNativePromptApi().CaptureWindowSnapshot(
                dialog, policy, Environment.ProcessId);
            if (!expected)
            {
                Assert.Null(snapshot);
                return;
            }

            Assert.NotNull(snapshot);
            Assert.True(snapshot.UsesDialogEnterSubmit);
            Assert.Equal(
                NativePromptDialogEnterLayout.CaptionlessCcbKeyboard,
                snapshot.DialogEnterLayout);
            Assert.Equal(10005, snapshot.InputControlId);
            Assert.Equal(IntPtr.Zero, snapshot.SubmitControlHandle);
            AssertCapturedLayoutMatch(policy, snapshot, string.Empty, expected: true);
            Assert.False(new WindowsNativePromptApi().IsSubmitControlReady(snapshot));
        }
        finally
        {
            DestroyWindow(dialog);
        }
    }

    [Theory]
    [InlineData(false, "", "确  定", 1010, 1, 1, true)]
    [InlineData(false, "", "确  定", 2222, 88, 1, true)]
    [InlineData(false, "验证网银盾密码", "确  认", 2222, 88, 0, true)]
    [InlineData(false, "", "确  定", 1010, 1, 0, false)]
    [InlineData(false, "", "确  定", 1010, 1, 2, false)]
    [InlineData(false, "保存文件", "确  定", 2222, 88, 1, false)]
    [InlineData(false, "", "取消", 1010, 1, 1, false)]
    [InlineData(true, "", "取消", 1010, 1, 1, false)]
    [InlineData(true, "", "确  定", 2222, 88, 1, true)]
    [InlineData(true, "验证网银盾密码", "确  定", 3743, 4000, 0, true)]
    public void Capture_ObservedDialog_ValidatesLayoutBeforeAcceptingEmptyTitle(
        bool configuredAlternative, string title, string buttonText, int inputId, int submitId,
        int cancelCount, bool expectedMatch)
    {
        // Off-screen, non-activating test windows exercise the actual Win32 capture path.
        // No PIN is read or sent, and the window is always destroyed on this same thread.
        var dialog = CreateWindowExW(0x08000000, "#32770", title, 0x10000000,
            -32000, -32000, 200, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, dialog);
        try
        {
            Assert.NotEqual(IntPtr.Zero, CreateWindowExW(0, "Edit", "", 0x50000000,
                0, 0, 80, 20, dialog, new IntPtr(inputId), IntPtr.Zero, IntPtr.Zero));
            Assert.NotEqual(IntPtr.Zero, CreateWindowExW(0, "Button", buttonText, 0x50000000,
                0, 25, 80, 20, dialog, new IntPtr(submitId), IntPtr.Zero, IntPtr.Zero));
            for (var index = 0; index < cancelCount; index++)
            {
                Assert.NotEqual(IntPtr.Zero, CreateWindowExW(0, "Button", "取  消", 0x50000000,
                    90, 25 + index * 25, 80, 20, dialog, new IntPtr(900 + index), IntPtr.Zero, IntPtr.Zero));
            }
            var policy = NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy() with
            {
                ProcessFileName = Path.GetFileName(Environment.ProcessPath),
                InputControlId = configuredAlternative ? inputId : 3743,
                SubmitControlId = configuredAlternative ? submitId : 4000
            };
            var snapshot = new WindowsNativePromptApi().CaptureWindowSnapshot(dialog, policy, Environment.ProcessId);
            if (snapshot is null)
            {
                Assert.False(expectedMatch);
                return;
            }

            Assert.Equal(inputId, snapshot.InputControlId);
            Assert.Equal(submitId, snapshot.SubmitControlId);
            AssertCapturedLayoutMatch(policy, snapshot, title, expectedMatch);
            var fresh = new WindowsNativePromptApi().CaptureWindowSnapshot(dialog, policy, Environment.ProcessId);
            Assert.NotNull(fresh);
            Assert.True(NativePromptWindowMatcher.HasSameIdentity(policy, snapshot, fresh!));
            if (expectedMatch && title.Length == 0)
            {
                Assert.True(DestroyWindow(GetDlgItem(dialog, 900)));
                Assert.Null(new WindowsNativePromptApi().CaptureWindowSnapshot(dialog, policy, Environment.ProcessId));
            }
        }
        finally
        {
            DestroyWindow(dialog);
        }
    }

    private void AssertCapturedLayoutMatch(ClientCertificateNativePromptOptions policy,
        NativePromptWindowSnapshot snapshot, string title, bool expected)
    {
        Assert.Equal(title, snapshot.WindowTitle);
        Assert.Equal(Environment.ProcessId, snapshot.ProcessId);
        using var process = Process.GetCurrentProcess();
        Assert.Equal(process.SessionId, snapshot.ProcessSessionId);
        Assert.Equal(process.SessionId, snapshot.CurrentInteractiveSessionId);
        Assert.Equal(snapshot.CurrentUserSid, snapshot.ProcessOwnerSid);
        Assert.True(snapshot.IntegrityRid > 0);
        output.WriteLine($"Captured test process integrity: 0x{snapshot.IntegrityRid:X}; session: {process.SessionId}");

        // Hosted Windows runners are elevated; the production matcher correctly refuses
        // their real high-integrity windows. Test the captured layout under the explicit
        // medium-integrity client fixture as well, without relaxing the production gate.
        Assert.Equal(expected && snapshot.IntegrityRid == NativePromptWindowMatcher.MediumIntegrityRid,
            NativePromptWindowMatcher.IsMatch(policy, snapshot, Environment.ProcessId));
        Assert.Equal(expected, NativePromptWindowMatcher.IsMatch(policy,
            snapshot with { IntegrityRid = NativePromptWindowMatcher.MediumIntegrityRid }, Environment.ProcessId));
        Assert.False(NativePromptWindowMatcher.IsMatch(policy,
            snapshot with { IntegrityRid = NativePromptWindowMatcher.HighIntegrityRid }, Environment.ProcessId));
    }

    private static void CreateCaptionlessCcbKeyboard(IntPtr dialog, int omittedControlId)
    {
        Assert.NotEqual(IntPtr.Zero, CreateWindowExW(
            0, "Edit", "", 0x50000020, 0, 0, 80, 20,
            dialog, new IntPtr(10005), IntPtr.Zero, IntPtr.Zero));
        for (var controlId = 1001; controlId <= 1047; controlId++)
        {
            if (controlId == omittedControlId) continue;
            Assert.NotEqual(IntPtr.Zero, CreateWindowExW(
                0, "Button", (controlId - 1000).ToString(
                    "00",
                    System.Globalization.CultureInfo.InvariantCulture), 0x40000000,
                0, 25, 30, 20, dialog, new IntPtr(controlId), IntPtr.Zero, IntPtr.Zero));
        }

        foreach (var (controlId, text) in new[]
        {
            (1069, "Caps Lock"),
            (1070, "←"),
            (1071, "OK")
        })
        {
            if (controlId == omittedControlId) continue;
            Assert.NotEqual(IntPtr.Zero, CreateWindowExW(
                0, "Button", text, 0x40000000,
                0, 50, 80, 20, dialog, new IntPtr(controlId), IntPtr.Zero, IntPtr.Zero));
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string text, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr window, int controlId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
