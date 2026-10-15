using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class NativePromptWindowMatcherTests
{
    internal const int TrustedClientProcessId = 420;

    public static TheoryData<string> IdentityDifferences => new()
    {
        "zero-hwnd",
        "not-top-level",
        "invisible",
        "disabled",
        "title",
        "class",
        "child-chain",
        "process-id",
        "path",
        "hash",
        "authenticode",
        "signer",
        "owner-sid",
        "current-user-sid",
        "session",
        "interactive-session",
        "integrity"
    };

    [Fact]
    public void IsMatch_AllIdentityAndWindowFieldsExactlyMatch_ReturnsTrue()
    {
        Assert.True(NativePromptWindowMatcher.IsMatch(Policy(), Candidate()));
    }

    [Theory]
    [MemberData(nameof(IdentityDifferences))]
    public void IsMatch_AnyIdentityOrWindowDifference_ReturnsFalse(string difference)
    {
        Assert.False(NativePromptWindowMatcher.IsMatch(Policy(), Mutate(Candidate(), difference)));
    }

    [Fact]
    public void IsMatch_DisabledPolicy_ReturnsFalse()
    {
        Assert.False(NativePromptWindowMatcher.IsMatch(Policy() with { Enabled = false }, Candidate()));
    }

    [Fact]
    public void IsMatch_MissingSecurityData_ReturnsFalse()
    {
        var candidate = Candidate() with
        {
            ProcessPath = string.Empty,
            ProcessSha256 = string.Empty,
            SignerCertificateThumbprint = string.Empty,
            ProcessOwnerSid = string.Empty,
            CurrentUserSid = string.Empty
        };

        Assert.False(NativePromptWindowMatcher.IsMatch(Policy(), candidate));
    }

    [Fact]
    public void IsMatch_MalformedHashAndThumbprintThatEqualPolicy_ReturnsFalse()
    {
        var policy = Policy() with
        {
            ProcessSha256 = "A",
            PublisherCertificateThumbprint = "B"
        };
        var candidate = Candidate() with
        {
            ProcessSha256 = "A",
            SignerCertificateThumbprint = "B"
        };

        Assert.False(NativePromptWindowMatcher.IsMatch(policy, candidate));
    }

    [Fact]
    public void IsMatch_InvalidEqualOwnerAndCurrentUserSids_ReturnsFalse()
    {
        var candidate = Candidate() with
        {
            ProcessOwnerSid = "not-a-sid",
            CurrentUserSid = "not-a-sid"
        };

        Assert.False(NativePromptWindowMatcher.IsMatch(Policy(), candidate));
    }

    [Fact]
    public void IsMatch_EmptyConfiguredAndObservedChildAncestry_ReturnsFalse()
    {
        var policy = Policy() with { RequiredChildWindowClasses = [] };
        var candidate = Candidate() with { DirectDescendantClassAncestry = [] };

        Assert.False(NativePromptWindowMatcher.IsMatch(policy, candidate));
    }

    [Fact]
    public void TryFindUniqueMatch_NoExactCandidate_ReturnsFalse()
    {
        var candidates = new[] { Candidate() with { ProcessSha256 = new string('0', 64) } };

        Assert.False(NativePromptWindowMatcher.TryFindUniqueMatch(Policy(), candidates, out var match));
        Assert.Null(match);
    }

    [Fact]
    public void TryFindUniqueMatch_TwoExactCandidates_ReturnsFalse()
    {
        var candidates = new[]
        {
            Candidate(),
            Candidate() with { WindowHandle = new IntPtr(0x2234), ProcessId = 201 }
        };

        Assert.False(NativePromptWindowMatcher.TryFindUniqueMatch(Policy(), candidates, out var match));
        Assert.Null(match);
    }

    [Fact]
    public void TryFindUniqueMatch_OneExactAmongRejectedCandidates_ReturnsThatExactSnapshot()
    {
        var exact = Candidate();
        var candidates = new[]
        {
            exact with { IsVisible = false },
            exact,
            exact with { IntegrityRid = 0x2000 }
        };

        Assert.True(NativePromptWindowMatcher.TryFindUniqueMatch(Policy(), candidates, out var match));
        Assert.Same(exact, match);
    }

    [Fact]
    public void IsMatch_TrustedClientDescendantWithExactRootProcessAndControls_ReturnsTrue()
    {
        Assert.True(NativePromptWindowMatcher.IsMatch(
            DescendantPolicy(),
            DescendantCandidate(),
            TrustedClientProcessId));
    }

    [Fact]
    public void IsMatch_TrustedClientOwnedTopLevelDialogWithExactOwnerRoot_ReturnsTrue()
    {
        var candidate = DescendantCandidate() with { IsTopLevel = true };

        Assert.True(NativePromptWindowMatcher.IsMatch(
            DescendantPolicy(),
            candidate,
            TrustedClientProcessId));
    }

    [Theory]
    [InlineData("wrong-root-process")]
    [InlineData("top-level-root-is-self")]
    [InlineData("wrong-root-title")]
    [InlineData("wrong-root-class")]
    [InlineData("wrong-process-file")]
    [InlineData("wrong-signer")]
    [InlineData("wrong-integrity")]
    [InlineData("missing-input")]
    [InlineData("missing-submit")]
    public void IsMatch_TrustedClientDescendantIdentityDifference_ReturnsFalse(string difference)
    {
        var candidate = difference switch
        {
            "wrong-root-process" => DescendantCandidate() with { RootProcessId = TrustedClientProcessId + 1 },
            "top-level-root-is-self" => DescendantCandidate() with
            {
                IsTopLevel = true,
                RootWindowHandle = DescendantCandidate().WindowHandle
            },
            "wrong-root-title" => DescendantCandidate() with { RootWindowTitle = "Other" },
            "wrong-root-class" => DescendantCandidate() with { RootWindowClass = "Other" },
            "wrong-process-file" => DescendantCandidate() with { ProcessPath = @"C:\Windows\notepad.exe" },
            "wrong-signer" => DescendantCandidate() with { SignerCertificateThumbprint = new string('F', 40) },
            "wrong-integrity" => DescendantCandidate() with { IntegrityRid = 0x3000 },
            "missing-input" => DescendantCandidate() with { InputControlHandle = IntPtr.Zero },
            "missing-submit" => DescendantCandidate() with { SubmitControlHandle = IntPtr.Zero },
            _ => throw new ArgumentOutOfRangeException(nameof(difference))
        };

        Assert.False(NativePromptWindowMatcher.IsMatch(
            DescendantPolicy(),
            candidate,
            TrustedClientProcessId));
    }

    [Fact]
    public void TryFindUniqueMatch_SimilarDescendantFromOtherLauncherInstance_IsRejected()
    {
        var exact = DescendantCandidate();
        var candidates = new[]
        {
            exact with { RootProcessId = TrustedClientProcessId + 1 },
            exact
        };

        Assert.True(NativePromptWindowMatcher.TryFindUniqueMatch(
            DescendantPolicy(),
            candidates,
            TrustedClientProcessId,
            out var match));
        Assert.Same(exact, match);
    }

    [Fact]
    public void IsMatch_TrustedClientDirectWebViewChildWithExactControls_ReturnsTrue()
    {
        Assert.True(NativePromptWindowMatcher.IsMatch(
            TrustedClientTopLevelPolicy(),
            TrustedClientTopLevelCandidate(),
            TrustedClientProcessId));
    }

    [Fact]
    public void IsMatch_TrustedClientTopLevelWithBaseTitle_ReturnsTrue()
    {
        Assert.True(NativePromptWindowMatcher.IsMatch(
            TrustedClientTopLevelPolicy(),
            TrustedClientTopLevelCandidate() with
            {
                WindowTitle = "验证通用盾密码"
            },
            TrustedClientProcessId));
    }

    [Theory]
    [InlineData("验证网银盾密码")]
    [InlineData("验证通用盾密码")]
    [InlineData("网银盾验证密码")]
    [InlineData("密码验证")]
    [InlineData("验证密码")]
    [InlineData("厂商提示：请输入密码完成验证（安全设备）")]
    [InlineData("验证网银盾密码 ")]
    [InlineData("验证网银盾密码 - 其他提示")]
    public void IsMatch_TrustedClientTopLevelAcceptsApprovedChineseTitleStructure(string title)
    {
        Assert.True(NativePromptWindowMatcher.IsMatch(
            TrustedClientTopLevelPolicy(),
            TrustedClientTopLevelCandidate() with { WindowTitle = title },
            TrustedClientProcessId));
    }

    [Fact]
    public void IsMatch_TrustedClientTopLevelWithObservedAlternateControlPair_ReturnsTrue()
    {
        var candidate = TrustedClientTopLevelCandidate() with
        {
            WindowTitle = "验证网银盾密码",
            InputControlId = 1010,
            SubmitControlId = 1,
            HasVerifiedAlternativeControls = true
        };

        Assert.True(NativePromptWindowMatcher.IsMatch(
            TrustedClientTopLevelPolicy(), candidate, TrustedClientProcessId));
    }

    [Theory]
    [InlineData("网银盾密码")]
    [InlineData("验证网银盾")]
    [InlineData("验证")]
    [InlineData("密码")]
    [InlineData("安全设备")]
    public void IsMatch_TrustedClientTopLevelRejectsUnapprovedChineseTitleStructure(string title)
    {
        Assert.False(NativePromptWindowMatcher.IsMatch(
            TrustedClientTopLevelPolicy(),
            TrustedClientTopLevelCandidate() with { WindowTitle = title },
            TrustedClientProcessId));
    }

    [Fact]
    public void IsMatch_TrustedClientTopLevelWithExtraTitleSuffix_ReturnsTrue()
    {
        Assert.True(NativePromptWindowMatcher.IsMatch(
            TrustedClientTopLevelPolicy(),
            TrustedClientTopLevelCandidate() with
            {
                WindowTitle = "验证通用盾密码 - 其他提示"
            },
            TrustedClientProcessId));
    }

    [Fact]
    public void IsMatch_TrustedClientProcessItselfWithExactControls_ReturnsTrue()
    {
        var policy = TrustedClientTopLevelPolicy() with
        {
            ProcessFileName = "UsbWebLauncher.App.exe"
        };
        var candidate = TrustedClientTopLevelCandidate() with
        {
            ProcessId = TrustedClientProcessId,
            ParentProcessId = 100,
            ProcessPath = @"C:\Program Files\UsbWebLauncher\UsbWebLauncher.App.exe",
            RootProcessId = TrustedClientProcessId
        };

        Assert.True(NativePromptWindowMatcher.IsMatch(
            policy,
            candidate,
            TrustedClientProcessId));
    }

    [Theory]
    [InlineData("wrong-process")]
    [InlineData("missing-parent")]
    [InlineData("wrong-parent")]
    [InlineData("grandchild")]
    [InlineData("not-top-level")]
    [InlineData("wrong-root")]
    [InlineData("wrong-root-process")]
    [InlineData("wrong-title")]
    [InlineData("wrong-process-file")]
    [InlineData("wrong-integrity")]
    [InlineData("missing-input")]
    [InlineData("missing-submit")]
    public void IsMatch_TrustedClientTopLevelIdentityDifference_ReturnsFalse(string difference)
    {
        var candidate = difference switch
        {
            "wrong-process" => TrustedClientTopLevelCandidate() with
            {
                ProcessId = TrustedClientProcessId + 1,
                ParentProcessId = TrustedClientProcessId + 2,
                RootProcessId = TrustedClientProcessId + 1
            },
            "missing-parent" => TrustedClientTopLevelCandidate() with { ParentProcessId = 0 },
            "wrong-parent" => TrustedClientTopLevelCandidate() with
            {
                ParentProcessId = TrustedClientProcessId + 1
            },
            "grandchild" => TrustedClientTopLevelCandidate() with
            {
                ProcessId = 501,
                ParentProcessId = 500,
                RootProcessId = 501
            },
            "not-top-level" => TrustedClientTopLevelCandidate() with { IsTopLevel = false },
            "wrong-root" => TrustedClientTopLevelCandidate() with
            {
                RootWindowHandle = new IntPtr(0x9999)
            },
            "wrong-root-process" => TrustedClientTopLevelCandidate() with
            {
                RootProcessId = TrustedClientProcessId
            },
            "wrong-title" => TrustedClientTopLevelCandidate() with { WindowTitle = "Other prompt" },
            "wrong-process-file" => TrustedClientTopLevelCandidate() with
            {
                ProcessPath = @"C:\Program Files\UsbWebLauncher\Other.exe"
            },
            "wrong-integrity" => TrustedClientTopLevelCandidate() with { IntegrityRid = 0x3000 },
            "missing-input" => TrustedClientTopLevelCandidate() with
            {
                InputControlHandle = IntPtr.Zero
            },
            "missing-submit" => TrustedClientTopLevelCandidate() with
            {
                SubmitControlHandle = IntPtr.Zero
            },
            _ => throw new ArgumentOutOfRangeException(nameof(difference))
        };

        Assert.False(NativePromptWindowMatcher.IsMatch(
            TrustedClientTopLevelPolicy(),
            candidate,
            TrustedClientProcessId));
    }

    [Fact]
    public void HasSameIdentity_DifferentParentProcessId_ReturnsFalse()
    {
        var candidate = TrustedClientTopLevelCandidate();

        Assert.False(NativePromptWindowMatcher.HasSameIdentity(
            candidate,
            candidate with { ParentProcessId = candidate.ParentProcessId + 1 }));
    }

    [Fact]
    public void HasSameIdentity_DifferentDialogEnterLayout_ReturnsFalse()
    {
        var candidate = TrustedClientTopLevelCandidate();

        Assert.False(NativePromptWindowMatcher.HasSameIdentity(
            candidate,
            candidate with
            {
                DialogEnterLayout = NativePromptDialogEnterLayout.CaptionlessCcbKeyboard
            }));
    }

    [Fact]
    public void HasSameIdentity_TrustedClientTopLevelApprovedTitleTransition_ReturnsTrue()
    {
        var policy = TrustedClientTopLevelPolicy();
        var candidate = TrustedClientTopLevelCandidate();

        Assert.True(NativePromptWindowMatcher.HasSameIdentity(
            policy,
            candidate with { WindowTitle = policy.WindowTitle! },
            candidate));
    }

    [Theory]
    [InlineData("验证通用盾密码")]
    [InlineData("网银盾验证密码")]
    [InlineData("密码验证")]
    [InlineData("验证密码 - 其他提示")]
    public void HasSameIdentity_TrustedClientTopLevelAllowsApprovedChineseTitleFamilyTransition(string title)
    {
        var policy = TrustedClientTopLevelPolicy();
        var candidate = TrustedClientTopLevelCandidate();

        Assert.True(NativePromptWindowMatcher.HasSameIdentity(
            policy,
            candidate with { WindowTitle = "验证网银盾密码" },
            candidate with { WindowTitle = title }));
    }

    [Fact]
    public void HasSameIdentity_TrustedClientTopLevelUnapprovedTitleTransition_ReturnsFalse()
    {
        var policy = TrustedClientTopLevelPolicy();
        var candidate = TrustedClientTopLevelCandidate();

        Assert.False(NativePromptWindowMatcher.HasSameIdentity(
            policy,
            candidate,
            candidate with { WindowTitle = "网银盾密码" }));
    }

    [Fact]
    public void TryFindUniqueControl_TwoMatchingPasswordInputs_FailsClosed()
    {
        var controls = new[]
        {
            Control(new IntPtr(0x3001), "Edit", 1010),
            Control(new IntPtr(0x3002), "Edit", 1010)
        };

        Assert.False(NativePromptWindowMatcher.TryFindUniqueControl(
            controls,
            expectedProcessId: 500,
            requiredClass: "Edit",
            requiredControlId: 1010,
            requireEnabled: true,
            out var match));
        Assert.Equal(IntPtr.Zero, match);
    }

    [Fact]
    public void TryFindUniqueControl_OneExactAmongRejectedControls_ReturnsExactHandle()
    {
        var exact = Control(new IntPtr(0x3001), "Edit", 1010);
        var controls = new[]
        {
            exact with { IsEnabled = false },
            exact with { WindowClass = "Button" },
            exact with { ProcessId = 501 },
            exact
        };

        Assert.True(NativePromptWindowMatcher.TryFindUniqueControl(
            controls,
            expectedProcessId: 500,
            requiredClass: "Edit",
            requiredControlId: 1010,
            requireEnabled: true,
            out var match));
        Assert.Equal(exact.WindowHandle, match);
    }

    [Fact]
    public void TryFindUniqueControl_DisabledSubmitAllowedWhenEnabledNotRequired()
    {
        var submit = Control(new IntPtr(0x4001), "Button", 4000) with
        {
            IsEnabled = false
        };

        Assert.True(NativePromptWindowMatcher.TryFindUniqueControl(
            [submit],
            expectedProcessId: 500,
            requiredClass: "Button",
            requiredControlId: 4000,
            requireEnabled: false,
            out var match));
        Assert.Equal(submit.WindowHandle, match);
    }

    internal static ClientCertificateNativePromptOptions Policy(int timeoutMilliseconds = 50) => new(
        Enabled: true,
        BrokerPipeName: "UsbWebLauncher.Tests.NativePrompt",
        PolicyId: "unit-test-policy",
        ProcessPath: @"C:\Program Files (x86)\Vendor\VendorPrompt.exe",
        ProcessSha256: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
        PublisherCertificateThumbprint: "0123456789ABCDEF0123456789ABCDEF01234567",
        WindowTitle: "Vendor security component",
        WindowClass: "#32770",
        RequiredChildWindowClasses: new[]
        {
            "Shell Embedding",
            "Shell DocObject View",
            "Internet Explorer_Server"
        },
        TimeoutMilliseconds: timeoutMilliseconds);

    internal static ClientCertificateNativePromptOptions DescendantPolicy(
        int timeoutMilliseconds = 50) => Policy(timeoutMilliseconds) with
    {
        WindowScope = NativePromptWindowScopes.TrustedClientDescendant,
        ProcessPath = string.Empty,
        ProcessSha256 = string.Empty,
        ProcessFileName = "msedgewebview2.exe",
        PublisherCertificateThumbprint = "4028CAD637509D4744B17EC5B42AED8D7A31E6AF",
        WindowTitle = string.Empty,
        RequiredChildWindowClasses = [],
        TrustedRootWindowTitle = "USB Web Launcher",
        TrustedRootWindowClassPrefix = "HwndWrapper[UsbWebLauncher.App;",
        InputWindowClass = "Edit",
        InputControlId = 1010,
        SubmitWindowClass = "Button",
        SubmitControlId = 1
    };

    internal static ClientCertificateNativePromptOptions TrustedClientTopLevelPolicy(
        int timeoutMilliseconds = 50) => Policy(timeoutMilliseconds) with
    {
        WindowScope = NativePromptWindowScopes.TrustedClientTopLevel,
        ProcessPath = string.Empty,
        ProcessSha256 = string.Empty,
        ProcessFileName = "msedgewebview2.exe",
        PublisherCertificateThumbprint = string.Empty,
        WindowTitle = "验证通用盾密码",
        RequiredChildWindowClasses = [],
        TrustedRootWindowTitle = string.Empty,
        TrustedRootWindowClassPrefix = string.Empty,
        InputWindowClass = "Edit",
        InputControlId = 3743,
        SubmitWindowClass = "Button",
        SubmitControlId = 4000
    };

    internal static NativePromptWindowSnapshot Candidate() => new(
        WindowHandle: new IntPtr(0x1234),
        IsTopLevel: true,
        IsVisible: true,
        IsEnabled: true,
        WindowTitle: "Vendor security component",
        WindowClass: "#32770",
        DirectDescendantClassAncestry: new[]
        {
            "Shell Embedding",
            "Shell DocObject View",
            "Internet Explorer_Server"
        },
        ProcessId: 200,
        ProcessPath: @"C:\Program Files (x86)\Vendor\VendorPrompt.exe",
        ProcessSha256: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
        IsAuthenticodeValid: true,
        SignerCertificateThumbprint: "0123456789ABCDEF0123456789ABCDEF01234567",
        ProcessOwnerSid: "S-1-5-21-1000",
        CurrentUserSid: "S-1-5-21-1000",
        ProcessSessionId: 3,
        CurrentInteractiveSessionId: 3,
        IntegrityRid: 0x3000);

    internal static NativePromptWindowSnapshot DescendantCandidate() => new(
        WindowHandle: new IntPtr(0x2000),
        IsTopLevel: false,
        IsVisible: true,
        IsEnabled: true,
        WindowTitle: string.Empty,
        WindowClass: "#32770",
        DirectDescendantClassAncestry: [],
        ProcessId: 500,
        ProcessPath: @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\152.0.4191.66\msedgewebview2.exe",
        ProcessSha256: "3437EA2F12904B7B7BE82C228F137C0631439E9A40CDA5DA3970615F825D6D74",
        IsAuthenticodeValid: true,
        SignerCertificateThumbprint: "4028CAD637509D4744B17EC5B42AED8D7A31E6AF",
        ProcessOwnerSid: "S-1-5-21-1000",
        CurrentUserSid: "S-1-5-21-1000",
        ProcessSessionId: 3,
        CurrentInteractiveSessionId: 3,
        IntegrityRid: 0x2000,
        RootWindowHandle: new IntPtr(0x1000),
        RootProcessId: TrustedClientProcessId,
        RootWindowTitle: "USB Web Launcher",
        RootWindowClass: "HwndWrapper[UsbWebLauncher.App;;123]",
        InputControlHandle: new IntPtr(0x2001),
        InputWindowClass: "Edit",
        InputControlId: 1010,
        SubmitControlHandle: new IntPtr(0x2002),
        SubmitWindowClass: "Button",
        SubmitControlId: 1);

    internal static NativePromptWindowSnapshot TrustedClientTopLevelCandidate() => new(
        WindowHandle: new IntPtr(0x4000),
        IsTopLevel: true,
        IsVisible: true,
        IsEnabled: true,
        WindowTitle: "验证通用盾密码 - 大写锁定打开！",
        WindowClass: "#32770",
        DirectDescendantClassAncestry: [],
        ProcessId: 500,
        ProcessPath: @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\152.0.4191.66\msedgewebview2.exe",
        ProcessSha256: "1838EFCF696E1F7832936DE9E640AA2C85F982F5CFFCF0E398A18A92082220A3",
        IsAuthenticodeValid: false,
        SignerCertificateThumbprint: string.Empty,
        ProcessOwnerSid: "S-1-5-21-1000",
        CurrentUserSid: "S-1-5-21-1000",
        ProcessSessionId: 3,
        CurrentInteractiveSessionId: 3,
        IntegrityRid: 0x2000,
        RootWindowHandle: new IntPtr(0x4000),
        RootProcessId: 500,
        InputControlHandle: new IntPtr(0x4001),
        InputWindowClass: "Edit",
        InputControlId: 3743,
        SubmitControlHandle: new IntPtr(0x4002),
        SubmitWindowClass: "Button",
        SubmitControlId: 4000,
        ParentProcessId: TrustedClientProcessId);

    private static NativePromptControlSnapshot Control(
        IntPtr handle,
        string windowClass,
        int controlId) => new(
            handle,
            IsVisible: true,
            IsEnabled: true,
            ProcessId: 500,
            windowClass,
            controlId);

    internal static NativePromptWindowSnapshot Mutate(
        NativePromptWindowSnapshot candidate,
        string difference) => difference switch
    {
        "zero-hwnd" => candidate with { WindowHandle = IntPtr.Zero },
        "not-top-level" => candidate with { IsTopLevel = false },
        "invisible" => candidate with { IsVisible = false },
        "disabled" => candidate with { IsEnabled = false },
        "title" => candidate with { WindowTitle = "Other title" },
        "class" => candidate with { WindowClass = "OtherClass" },
        "child-chain" => candidate with
        {
            DirectDescendantClassAncestry = new[]
            {
                "Shell Embedding",
                "Unexpected",
                "Internet Explorer_Server"
            }
        },
        "process-id" => candidate with { ProcessId = 0 },
        "path" => candidate with { ProcessPath = @"C:\Windows\System32\notepad.exe" },
        "hash" => candidate with { ProcessSha256 = new string('F', 64) },
        "authenticode" => candidate with { IsAuthenticodeValid = false },
        "signer" => candidate with { SignerCertificateThumbprint = new string('F', 40) },
        "owner-sid" => candidate with { ProcessOwnerSid = "S-1-5-21-2000" },
        "current-user-sid" => candidate with { CurrentUserSid = "S-1-5-21-3000" },
        "session" => candidate with { ProcessSessionId = 4 },
        "interactive-session" => candidate with { CurrentInteractiveSessionId = 5 },
        "integrity" => candidate with { IntegrityRid = 0x4000 },
        _ => throw new ArgumentOutOfRangeException(nameof(difference))
    };
}
