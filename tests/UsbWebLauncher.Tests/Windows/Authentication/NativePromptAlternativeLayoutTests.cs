using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class NativePromptAlternativeLayoutTests
{
    private static NativePromptControlSnapshot Input(int id = 1010) => new(new IntPtr(10), true, true, 500, "Edit", id);
    private static NativePromptControlSnapshot Submit(int id = 1, string text = "确  定") => new(new IntPtr(11), true, true, 500, "Button", id, text);

    [Fact]
    public void ObservedAlternativePair_IsSelectedWithActualIds()
    {
        Assert.True(Select([Input(), Submit(), Submit(2, "取消") with { WindowHandle = new IntPtr(12) }], out var input, out var submit));
        Assert.Equal(1010, input!.ControlId);
        Assert.Equal(1, submit!.ControlId);
    }

    [Fact]
    public void ConfiguredPair_RemainsSupportedWithDisabledSubmit()
    {
        Assert.True(Select([Input(3743), Submit(4000) with { IsEnabled = false }], out var input, out var submit));
        Assert.Equal(3743, input!.ControlId);
        Assert.Equal(4000, submit!.ControlId);
    }

    [Fact]
    public void ConfiguredPair_IsPreferredOverOtherVisibleControls()
    {
        Assert.True(Select([Input(3743), Submit(4000),
            Input(2222) with { WindowHandle = new IntPtr(13) }], out var input, out var submit));
        Assert.Equal(3743, input!.ControlId);
        Assert.Equal(4000, submit!.ControlId);
    }

    [Theory]
    [InlineData("取消")]
    [InlineData("")]
    [InlineData("确认交易")]
    public void AlternativePair_RejectsUnapprovedButton(string text) =>
        Assert.False(Select([Input(), Submit(text: text)], out _, out _));

    [Fact]
    public void AlternativePair_RejectsMultipleVisibleInputs() =>
        Assert.False(Select([Input(), Input(1011) with { WindowHandle = new IntPtr(13) }, Submit()], out _, out _));

    [Fact]
    public void AlternativePair_RejectsMultipleConfirmationButtons() =>
        Assert.False(Select([Input(), Submit(), Submit(3) with { WindowHandle = new IntPtr(13) }], out _, out _));

    [Theory]
    [InlineData(2222, 88)]
    [InlineData(3743, 88)]
    [InlineData(2222, 4000)]
    public void AlternativePair_AcceptsPositiveIdsIndependentOfDriver(int inputId, int submitId)
    {
        Assert.True(Select([Input(inputId), Submit(submitId)], out var input, out var submit));
        Assert.Equal(inputId, input!.ControlId);
        Assert.Equal(submitId, submit!.ControlId);
    }

    [Theory]
    [InlineData(0, 88)]
    [InlineData(-1, 88)]
    [InlineData(2222, 0)]
    [InlineData(2222, -1)]
    public void AlternativePair_RejectsNonpositiveIds(int inputId, int submitId) =>
        Assert.False(Select([Input(inputId), Submit(submitId)], out _, out _));

    [Fact]
    public void AlternativePair_IgnoresDisabledExtraInput() =>
        Assert.True(Select([Input(2222), Input(3333) with { WindowHandle = new IntPtr(13), IsEnabled = false }, Submit(88)], out _, out _));

    [Fact]
    public void AlternativePair_RejectsDifferentProcess() =>
        Assert.False(Select([Input(), Submit() with { ProcessId = 501 }], out _, out _));

    [Theory]
    [InlineData("")]
    [InlineData("验证网银盾密码")]
    public void VerifiedAlternativeSnapshot_AcceptsObservedTitle(string title)
    {
        var candidate = AlternativeCandidate() with { WindowTitle = title };
        Assert.True(NativePromptWindowMatcher.IsMatch(NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy(), candidate, NativePromptWindowMatcherTests.TrustedClientProcessId));
        Assert.True(NativePromptWindowMatcher.HasSameIdentity(candidate, candidate));
    }

    [Theory]
    [InlineData("unverified")]
    [InlineData("wrong-parent")]
    [InlineData("wrong-session")]
    [InlineData("wrong-title")]
    [InlineData("invalid-id")]
    public void AlternativeSnapshot_RejectsUntrustedIdentity(string difference)
    {
        var candidate = AlternativeCandidate();
        candidate = difference switch
        {
            "unverified" => candidate with { HasVerifiedAlternativeControls = false },
            "wrong-parent" => candidate with { ParentProcessId = 99 },
            "wrong-session" => candidate with { ProcessSessionId = 99 },
            "wrong-title" => candidate with { WindowTitle = "保存文件" },
            "invalid-id" => candidate with { InputControlId = 0 },
            _ => throw new ArgumentException(difference)
        };
        Assert.False(NativePromptWindowMatcher.IsMatch(NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy(), candidate, NativePromptWindowMatcherTests.TrustedClientProcessId));
    }

    [Fact]
    public void ConfiguredLayout_DoesNotAcceptEmptyTitle() =>
        Assert.False(NativePromptWindowMatcher.IsMatch(NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy(), NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate() with { WindowTitle = "" }, NativePromptWindowMatcherTests.TrustedClientProcessId));

    private static NativePromptWindowSnapshot AlternativeCandidate() => NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate() with
    {
        WindowTitle = "", InputControlId = 2222, SubmitControlId = 88, HasVerifiedAlternativeControls = true
    };

    private static bool Select(IReadOnlyList<NativePromptControlSnapshot> controls, out NativePromptControlSnapshot? input, out NativePromptControlSnapshot? submit) =>
        NativePromptWindowMatcher.TryFindTrustedClientTopLevelControls(controls, 500, NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy(), out input, out submit, out _);

    [Fact]
    public void ConfiguredAlternativeIds_DoNotImplyVerifiedAlternativeLayout()
    {
        var policy = NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy() with { InputControlId = 1010, SubmitControlId = 1 };
        Assert.True(NativePromptWindowMatcher.TryFindTrustedClientTopLevelControls(
            [Input(), Submit(text: "取消")], 500, policy, out _, out _, out var verifiedAlternative));
        Assert.False(verifiedAlternative);
        Assert.False(NativePromptWindowMatcher.IsMatch(policy,
            AlternativeCandidate() with { InputControlId = 1010, SubmitControlId = 1, HasVerifiedAlternativeControls = verifiedAlternative },
            NativePromptWindowMatcherTests.TrustedClientProcessId));
    }

    [Fact]
    public void CaptionlessCcbKeyboard_CompleteFingerprintSelectsPasswordInput()
    {
        var controls = CaptionlessCcbKeyboardControls();

        Assert.True(NativePromptWindowMatcher.TryFindCaptionlessCcbKeyboardInput(
            controls, 500, string.Empty, out var input));
        Assert.NotNull(input);
        Assert.Equal(10005, input.ControlId);
        Assert.True(input.IsPassword);
    }

    [Theory]
    [InlineData("nonempty-title")]
    [InlineData("missing-button")]
    [InlineData("wrong-button-text")]
    [InlineData("visible-button")]
    [InlineData("disabled-button")]
    [InlineData("wrong-process")]
    [InlineData("non-password-input")]
    [InlineData("hidden-input")]
    [InlineData("disabled-input")]
    [InlineData("wrong-input-id")]
    [InlineData("extra-edit")]
    [InlineData("extra-button")]
    [InlineData("duplicate-id")]
    public void CaptionlessCcbKeyboard_AlteredFingerprintIsRejected(string difference)
    {
        var title = string.Empty;
        var controls = CaptionlessCcbKeyboardControls().ToList();
        switch (difference)
        {
            case "nonempty-title":
                title = "验证网银盾密码";
                break;
            case "missing-button":
                controls.RemoveAll(control => control.ControlId == 1024);
                break;
            case "wrong-button-text":
                MutateControl(controls, 1024, control => control with { WindowText = "XX" });
                break;
            case "visible-button":
                MutateControl(controls, 1024, control => control with { IsVisible = true });
                break;
            case "disabled-button":
                MutateControl(controls, 1024, control => control with { IsEnabled = false });
                break;
            case "wrong-process":
                MutateControl(controls, 1024, control => control with { ProcessId = 501 });
                break;
            case "non-password-input":
                MutateControl(controls, 10005, control => control with { IsPassword = false });
                break;
            case "hidden-input":
                MutateControl(controls, 10005, control => control with { IsVisible = false });
                break;
            case "disabled-input":
                MutateControl(controls, 10005, control => control with { IsEnabled = false });
                break;
            case "wrong-input-id":
                MutateControl(controls, 10005, control => control with { ControlId = 10006 });
                break;
            case "extra-edit":
                controls.Add(new NativePromptControlSnapshot(
                    new IntPtr(20000), true, true, 500, "Edit", 20000, IsPassword: true));
                break;
            case "extra-button":
                controls.Add(new NativePromptControlSnapshot(
                    new IntPtr(20001), true, true, 500, "Button", 20001, "Extra"));
                break;
            case "duplicate-id":
                MutateControl(controls, 1025, control => control with { ControlId = 1024 });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(difference));
        }

        Assert.False(NativePromptWindowMatcher.TryFindCaptionlessCcbKeyboardInput(
            controls, 500, title, out _));
    }

    [Fact]
    public void CaptionlessCcbKeyboardSnapshot_IsAcceptedOnlyWithExplicitLayout()
    {
        var candidate = NativePromptWindowMatcherTests.TrustedClientTopLevelCandidate() with
        {
            WindowTitle = string.Empty,
            InputControlId = 10005,
            SubmitControlHandle = IntPtr.Zero,
            SubmitWindowClass = string.Empty,
            SubmitControlId = 0,
            HasVerifiedAlternativeControls = true,
            UsesDialogEnterSubmit = true,
            DialogEnterLayout = NativePromptDialogEnterLayout.CaptionlessCcbKeyboard
        };

        Assert.True(NativePromptWindowMatcher.IsMatch(
            NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy(),
            candidate,
            NativePromptWindowMatcherTests.TrustedClientProcessId));
        Assert.False(NativePromptWindowMatcher.IsMatch(
            NativePromptWindowMatcherTests.TrustedClientTopLevelPolicy(),
            candidate with { DialogEnterLayout = NativePromptDialogEnterLayout.None },
            NativePromptWindowMatcherTests.TrustedClientProcessId));
    }

    private static IReadOnlyList<NativePromptControlSnapshot> CaptionlessCcbKeyboardControls()
    {
        var controls = new List<NativePromptControlSnapshot>
        {
            new(new IntPtr(10005), true, true, 500, "Edit", 10005, IsPassword: true)
        };
        for (var controlId = 1001; controlId <= 1047; controlId++)
        {
            controls.Add(new NativePromptControlSnapshot(
                new IntPtr(controlId),
                IsVisible: false,
                IsEnabled: true,
                ProcessId: 500,
                WindowClass: "Button",
                ControlId: controlId,
                WindowText: (controlId - 1000).ToString(
                    "00",
                    System.Globalization.CultureInfo.InvariantCulture)));
        }

        controls.Add(new NativePromptControlSnapshot(
            new IntPtr(1069), false, true, 500, "Button", 1069, "Caps Lock"));
        controls.Add(new NativePromptControlSnapshot(
            new IntPtr(1070), false, true, 500, "Button", 1070, "←"));
        controls.Add(new NativePromptControlSnapshot(
            new IntPtr(1071), false, true, 500, "Button", 1071, "OK"));
        return controls;
    }

    private static void MutateControl(
        IList<NativePromptControlSnapshot> controls,
        int controlId,
        Func<NativePromptControlSnapshot, NativePromptControlSnapshot> mutation)
    {
        var index = controls.ToList().FindIndex(control => control.ControlId == controlId);
        Assert.True(index >= 0);
        controls[index] = mutation(controls[index]);
    }
}
