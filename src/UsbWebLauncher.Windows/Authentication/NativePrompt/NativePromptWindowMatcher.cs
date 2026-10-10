using UsbWebLauncher.Core.Configuration;
using System.Security.Principal;

namespace UsbWebLauncher.Windows.Authentication.NativePrompt;

internal sealed record NativePromptControlSnapshot(
    IntPtr WindowHandle,
    bool IsVisible,
    bool IsEnabled,
    int ProcessId,
    string WindowClass,
    int ControlId,
    string WindowText = "",
    bool IsPassword = false);

internal static class NativePromptWindowMatcher
{
    internal const int HighIntegrityRid = 0x00003000;
    private const string CapsLockTitleSuffix = " - 大写锁定打开！";

    internal static bool TryFindTrustedClientTopLevelControls(
        IReadOnlyList<NativePromptControlSnapshot> candidates,
        int expectedProcessId,
        ClientCertificateNativePromptOptions policy,
        out NativePromptControlSnapshot? input,
        out NativePromptControlSnapshot? submit,
        out bool verifiedAlternative,
        bool requireCaptionlessEvidence = false)
    {
        input = null;
        submit = null;
        verifiedAlternative = false;
        if (policy.InputControlId is not > 0 || policy.SubmitControlId is not > 0 ||
            string.IsNullOrEmpty(policy.InputWindowClass) || string.IsNullOrEmpty(policy.SubmitWindowClass))
        {
            return false;
        }

        // A configured pair remains preferred for titled dialogs. Captionless dialogs
        // must also prove their purpose through the confirmation and cancellation buttons.
        if (!requireCaptionlessEvidence &&
            TryFindUniqueControl(candidates, expectedProcessId, policy.InputWindowClass,
                policy.InputControlId.Value, true, out var inputHandle) &&
            TryFindUniqueControl(candidates, expectedProcessId, policy.SubmitWindowClass,
                policy.SubmitControlId.Value, false, out var submitHandle) && inputHandle != submitHandle)
        {
            input = candidates.First(control => control.WindowHandle == inputHandle);
            submit = candidates.First(control => control.WindowHandle == submitHandle);
            return true;
        }

        // Driver versions assign different control IDs. Match the unambiguous visible
        // roles instead; never read an Edit's contents to identify a password prompt.
        var visible = candidates.Where(control => control.WindowHandle != IntPtr.Zero &&
            control.IsVisible && control.ProcessId == expectedProcessId).ToArray();
        if (policy.InputWindowClass != "Edit" || policy.SubmitWindowClass != "Button")
        {
            return false;
        }

        var inputs = visible.Where(control => control.WindowClass == "Edit" && control.IsEnabled).ToArray();
        var confirms = visible.Where(control => control.WindowClass == "Button" &&
            IsConfirmationButtonText(control.WindowText)).ToArray();
        if (inputs.Length != 1 || inputs[0].ControlId <= 0 ||
            confirms.Length != 1 || confirms[0].ControlId <= 0 ||
            inputs[0].WindowHandle == confirms[0].WindowHandle)
        {
            return false;
        }

        if (requireCaptionlessEvidence)
        {
            var cancellations = visible.Where(control => control.WindowClass == "Button" &&
                NormalizeButtonText(control.WindowText) == "取消").ToArray();
            if (cancellations.Length != 1 || cancellations[0].WindowHandle == confirms[0].WindowHandle)
            {
                return false;
            }
        }

        input = inputs[0];
        submit = confirms[0];
        verifiedAlternative = true;
        return true;
    }

    private static bool IsConfirmationButtonText(string text) =>
        NormalizeButtonText(text) is "确定" or "确认";

    internal static bool TryFindDialogEnterInput(
        IReadOnlyList<NativePromptControlSnapshot> candidates, int processId, string title,
        out NativePromptControlSnapshot? input)
    {
        input = null;
        if (!IsApprovedTrustedClientTopLevelTitle(title)) return false;
        var visible = candidates.Where(control => control.WindowHandle != IntPtr.Zero &&
            control.IsVisible && control.ProcessId == processId).ToArray();
        if (visible.Any(control => control.WindowClass == "Button")) return false;
        var edits = visible.Where(control => control.WindowClass == "Edit").ToArray();
        if (edits.Length != 1 || !edits[0].IsEnabled || !edits[0].IsPassword || edits[0].ControlId <= 0)
            return false;
        input = edits[0];
        return true;
    }

    private static string NormalizeButtonText(string text) =>
        new(text.Where(character => !char.IsWhiteSpace(character)).ToArray());

    public static bool IsMatch(
        ClientCertificateNativePromptOptions policy,
        NativePromptWindowSnapshot candidate,
        int trustedClientProcessId = 0)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(candidate);

        var isTrustedClientTopLevel = IsTrustedClientTopLevel(policy);
        return policy.Enabled &&
               candidate.WindowHandle != IntPtr.Zero &&
               candidate.IsVisible &&
               candidate.IsEnabled &&
               candidate.ProcessId > 0 &&
               HasRequiredValue(candidate.WindowClass) &&
               HasRequiredValue(candidate.ProcessPath) &&
               IsUpperHex(candidate.ProcessSha256, 64) &&
               HasRequiredValue(candidate.ProcessOwnerSid) &&
               HasRequiredValue(candidate.CurrentUserSid) &&
               IsValidSid(candidate.ProcessOwnerSid) &&
               IsValidSid(candidate.CurrentUserSid) &&
               candidate.ProcessSessionId >= 0 &&
               candidate.CurrentInteractiveSessionId >= 0 &&
               string.Equals(candidate.WindowClass, policy.WindowClass, StringComparison.Ordinal) &&
               string.Equals(candidate.ProcessOwnerSid, candidate.CurrentUserSid, StringComparison.Ordinal) &&
               candidate.ProcessSessionId == candidate.CurrentInteractiveSessionId &&
               (isTrustedClientTopLevel
                   ? IsTrustedClientTopLevelMatch(policy, candidate, trustedClientProcessId)
                   : candidate.IsAuthenticodeValid &&
                     IsUpperHex(candidate.SignerCertificateThumbprint, 40) &&
                     IsUpperHex(policy.PublisherCertificateThumbprint, 40) &&
                     string.Equals(
                         candidate.SignerCertificateThumbprint,
                         policy.PublisherCertificateThumbprint,
                         StringComparison.Ordinal) &&
                     (IsTrustedClientDescendant(policy)
                         ? IsTrustedClientDescendantMatch(policy, candidate, trustedClientProcessId)
                         : IsTopLevelProcessMatch(policy, candidate)));
    }

    public static bool TryFindUniqueMatch(
        ClientCertificateNativePromptOptions policy,
        IReadOnlyList<NativePromptWindowSnapshot> candidates,
        int trustedClientProcessId,
        out NativePromptWindowSnapshot? match)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(candidates);

        match = null;
        foreach (var candidate in candidates)
        {
            if (candidate is null || !IsMatch(policy, candidate, trustedClientProcessId))
            {
                continue;
            }

            if (match is not null)
            {
                match = null;
                return false;
            }

            match = candidate;
        }

        return match is not null;
    }

    public static bool TryFindUniqueMatch(
        ClientCertificateNativePromptOptions policy,
        IReadOnlyList<NativePromptWindowSnapshot> candidates,
        out NativePromptWindowSnapshot? match) =>
        TryFindUniqueMatch(policy, candidates, trustedClientProcessId: 0, out match);

    internal static bool TryFindUniqueControl(
        IReadOnlyList<NativePromptControlSnapshot> candidates,
        int expectedProcessId,
        string requiredClass,
        int requiredControlId,
        bool requireEnabled,
        out IntPtr match)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        match = IntPtr.Zero;
        foreach (var candidate in candidates)
        {
            if (candidate is null ||
                candidate.WindowHandle == IntPtr.Zero ||
                !candidate.IsVisible ||
                (requireEnabled && !candidate.IsEnabled) ||
                candidate.ProcessId != expectedProcessId ||
                candidate.ControlId != requiredControlId ||
                !string.Equals(candidate.WindowClass, requiredClass, StringComparison.Ordinal))
            {
                continue;
            }

            if (match != IntPtr.Zero)
            {
                match = IntPtr.Zero;
                return false;
            }

            match = candidate.WindowHandle;
        }

        return match != IntPtr.Zero;
    }

    internal static bool HasSameIdentity(
        NativePromptWindowSnapshot first,
        NativePromptWindowSnapshot second) =>
        HasSameIdentity(first, second, titlesMatch: string.Equals(
            first.WindowTitle,
            second.WindowTitle,
            StringComparison.Ordinal));

    internal static bool HasSameIdentity(
        ClientCertificateNativePromptOptions policy,
        NativePromptWindowSnapshot first,
        NativePromptWindowSnapshot second)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return HasSameIdentity(
            first,
            second,
            titlesMatch: HasSameApprovedWindowTitle(policy, first, second));
    }

    private static bool HasSameIdentity(
        NativePromptWindowSnapshot first,
        NativePromptWindowSnapshot second,
        bool titlesMatch) =>
        first.WindowHandle == second.WindowHandle &&
        first.IsTopLevel == second.IsTopLevel &&
        first.IsVisible == second.IsVisible &&
        first.IsEnabled == second.IsEnabled &&
        first.ProcessId == second.ProcessId &&
        first.ParentProcessId == second.ParentProcessId &&
        first.ProcessSessionId == second.ProcessSessionId &&
        first.CurrentInteractiveSessionId == second.CurrentInteractiveSessionId &&
        first.IntegrityRid == second.IntegrityRid &&
        first.RootWindowHandle == second.RootWindowHandle &&
        first.RootProcessId == second.RootProcessId &&
        first.InputControlHandle == second.InputControlHandle &&
        first.InputControlId == second.InputControlId &&
        first.SubmitControlHandle == second.SubmitControlHandle &&
        first.SubmitControlId == second.SubmitControlId &&
        first.HasVerifiedAlternativeControls == second.HasVerifiedAlternativeControls &&
        first.UsesDialogEnterSubmit == second.UsesDialogEnterSubmit &&
        first.IsAuthenticodeValid == second.IsAuthenticodeValid &&
        titlesMatch &&
        string.Equals(first.WindowClass, second.WindowClass, StringComparison.Ordinal) &&
        string.Equals(first.ProcessPath, second.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.ProcessSha256, second.ProcessSha256, StringComparison.Ordinal) &&
        string.Equals(
            first.SignerCertificateThumbprint,
            second.SignerCertificateThumbprint,
            StringComparison.Ordinal) &&
        string.Equals(first.ProcessOwnerSid, second.ProcessOwnerSid, StringComparison.Ordinal) &&
        string.Equals(first.CurrentUserSid, second.CurrentUserSid, StringComparison.Ordinal) &&
        string.Equals(first.RootWindowTitle, second.RootWindowTitle, StringComparison.Ordinal) &&
        string.Equals(first.RootWindowClass, second.RootWindowClass, StringComparison.Ordinal) &&
        string.Equals(first.InputWindowClass, second.InputWindowClass, StringComparison.Ordinal) &&
        string.Equals(first.SubmitWindowClass, second.SubmitWindowClass, StringComparison.Ordinal) &&
        SequencesEqual(
            first.DirectDescendantClassAncestry,
            second.DirectDescendantClassAncestry);

    private static bool HasSameApprovedWindowTitle(
        ClientCertificateNativePromptOptions policy,
        NativePromptWindowSnapshot first,
        NativePromptWindowSnapshot second) =>
        string.Equals(first.WindowTitle, second.WindowTitle, StringComparison.Ordinal) ||
        (IsTrustedClientTopLevel(policy) &&
         HasRequiredValue(policy.WindowTitle) &&
         HasApprovedTrustedClientTopLevelTitle(policy.WindowTitle!, first.WindowTitle) &&
         HasApprovedTrustedClientTopLevelTitle(policy.WindowTitle!, second.WindowTitle));

    private static bool IsTrustedClientDescendantMatch(
        ClientCertificateNativePromptOptions policy,
        NativePromptWindowSnapshot candidate,
        int trustedClientProcessId) =>
        trustedClientProcessId > 0 &&
        candidate.IntegrityRid == MediumIntegrityRid &&
        candidate.RootWindowHandle != IntPtr.Zero &&
        candidate.RootWindowHandle != candidate.WindowHandle &&
        candidate.RootProcessId == trustedClientProcessId &&
        candidate.InputControlHandle != IntPtr.Zero &&
        candidate.SubmitControlHandle != IntPtr.Zero &&
        candidate.InputControlHandle != candidate.SubmitControlHandle &&
        !candidate.UsesDialogEnterSubmit &&
        HasRequiredValue(policy.InputWindowClass) &&
        string.Equals(candidate.InputWindowClass, policy.InputWindowClass, StringComparison.Ordinal) &&
        policy.InputControlId is > 0 &&
        candidate.InputControlId == policy.InputControlId &&
        HasRequiredValue(policy.SubmitWindowClass) &&
        string.Equals(candidate.SubmitWindowClass, policy.SubmitWindowClass, StringComparison.Ordinal) &&
        policy.SubmitControlId is > 0 &&
        candidate.SubmitControlId == policy.SubmitControlId &&
        HasRequiredValue(policy.ProcessFileName) &&
        string.Equals(
            Path.GetFileName(candidate.ProcessPath),
            policy.ProcessFileName,
            StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrEmpty(policy.ProcessSha256) ||
            string.Equals(candidate.ProcessSha256, policy.ProcessSha256, StringComparison.Ordinal)) &&
        string.Equals(candidate.WindowTitle, policy.WindowTitle, StringComparison.Ordinal) &&
        string.Equals(candidate.RootWindowTitle, policy.TrustedRootWindowTitle, StringComparison.Ordinal) &&
        HasRequiredValue(policy.TrustedRootWindowClassPrefix) &&
        candidate.RootWindowClass.StartsWith(
            policy.TrustedRootWindowClassPrefix!,
            StringComparison.Ordinal);

    private static bool IsTopLevelProcessMatch(
        ClientCertificateNativePromptOptions policy,
        NativePromptWindowSnapshot candidate) =>
        candidate.IsTopLevel &&
        candidate.IntegrityRid == HighIntegrityRid &&
        IsUpperHex(policy.ProcessSha256, 64) &&
        policy.RequiredChildWindowClasses is { Count: > 0 } &&
        candidate.DirectDescendantClassAncestry is { Count: > 0 } &&
        HasRequiredValue(candidate.WindowTitle) &&
        string.Equals(candidate.WindowTitle, policy.WindowTitle, StringComparison.Ordinal) &&
        PathsEqual(candidate.ProcessPath, policy.ProcessPath) &&
        string.Equals(candidate.ProcessSha256, policy.ProcessSha256, StringComparison.Ordinal) &&
        SequencesEqual(candidate.DirectDescendantClassAncestry, policy.RequiredChildWindowClasses);

    private static bool IsTrustedClientTopLevelMatch(
        ClientCertificateNativePromptOptions policy,
        NativePromptWindowSnapshot candidate,
        int trustedClientProcessId) =>
        trustedClientProcessId > 0 &&
        IsTrustedClientOrDirectChild(candidate, trustedClientProcessId) &&
        candidate.IsTopLevel &&
        candidate.IntegrityRid == MediumIntegrityRid &&
        candidate.RootWindowHandle == candidate.WindowHandle &&
        candidate.RootProcessId == candidate.ProcessId &&
        candidate.InputControlHandle != IntPtr.Zero &&
        (HasDialogEnterControls(candidate) ||
         (!candidate.UsesDialogEnterSubmit && candidate.SubmitControlHandle != IntPtr.Zero &&
          candidate.InputControlHandle != candidate.SubmitControlHandle)) &&
        HasRequiredValue(policy.InputWindowClass) &&
        string.Equals(candidate.InputWindowClass, policy.InputWindowClass, StringComparison.Ordinal) &&
        policy.InputControlId is > 0 &&
        (candidate.InputControlId == policy.InputControlId || HasAlternativeControlPair(candidate) || HasDialogEnterControls(candidate)) &&
        HasRequiredValue(policy.SubmitWindowClass) &&
        (HasDialogEnterControls(candidate) || string.Equals(candidate.SubmitWindowClass, policy.SubmitWindowClass, StringComparison.Ordinal)) &&
        policy.SubmitControlId is > 0 &&
        (candidate.SubmitControlId == policy.SubmitControlId || HasAlternativeControlPair(candidate) || HasDialogEnterControls(candidate)) &&
        HasRequiredValue(policy.ProcessFileName) &&
        string.Equals(
            Path.GetFileName(candidate.ProcessPath),
            policy.ProcessFileName,
            StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrEmpty(policy.ProcessSha256) ||
            string.Equals(candidate.ProcessSha256, policy.ProcessSha256, StringComparison.Ordinal)) &&
        HasRequiredValue(policy.WindowTitle) &&
        (HasApprovedTrustedClientTopLevelTitle(policy.WindowTitle!, candidate.WindowTitle) ||
         (candidate.WindowTitle.Length == 0 && HasAlternativeControlPair(candidate)));

    private static bool HasAlternativeControlPair(NativePromptWindowSnapshot candidate) =>
        !candidate.UsesDialogEnterSubmit && candidate.HasVerifiedAlternativeControls &&
        candidate.InputWindowClass == "Edit" && candidate.InputControlId > 0 &&
        candidate.SubmitWindowClass == "Button" && candidate.SubmitControlId > 0;

    private static bool HasDialogEnterControls(NativePromptWindowSnapshot candidate) =>
        candidate.UsesDialogEnterSubmit && candidate.HasVerifiedAlternativeControls &&
        IsApprovedTrustedClientTopLevelTitle(candidate.WindowTitle) &&
        candidate.InputWindowClass == "Edit" && candidate.InputControlId > 0 &&
        candidate.SubmitControlHandle == IntPtr.Zero && candidate.SubmitControlId == 0 &&
        candidate.SubmitWindowClass.Length == 0;

    private static bool IsTrustedClientOrDirectChild(
        NativePromptWindowSnapshot candidate,
        int trustedClientProcessId) =>
        candidate.ProcessId == trustedClientProcessId ||
        candidate.ParentProcessId == trustedClientProcessId;

    private static bool HasApprovedTrustedClientTopLevelTitle(
        string expectedTitle,
        string candidateTitle) =>
        IsApprovedTrustedClientTopLevelTitle(candidateTitle) &&
        (string.Equals(candidateTitle, expectedTitle, StringComparison.Ordinal) ||
         IsApprovedTrustedClientTopLevelTitle(expectedTitle) ||
         string.Equals(candidateTitle, expectedTitle + CapsLockTitleSuffix, StringComparison.Ordinal));

    private static bool IsApprovedTrustedClientTopLevelTitle(string title) =>
        title.Contains("验证", StringComparison.Ordinal) &&
        title.Contains("密码", StringComparison.Ordinal);

    private static bool IsTrustedClientDescendant(ClientCertificateNativePromptOptions policy) =>
        string.Equals(
            policy.WindowScope,
            NativePromptWindowScopes.TrustedClientDescendant,
            StringComparison.Ordinal);

    private static bool IsTrustedClientTopLevel(ClientCertificateNativePromptOptions policy) =>
        string.Equals(
            policy.WindowScope,
            NativePromptWindowScopes.TrustedClientTopLevel,
            StringComparison.Ordinal);

    internal const int MediumIntegrityRid = 0x00002000;

    private static bool HasRequiredValue(string? value) => !string.IsNullOrEmpty(value);

    private static bool IsUpperHex(string? value, int expectedLength) =>
        value is not null &&
        value.Length == expectedLength &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsValidSid(string value)
    {
        try
        {
            _ = new SecurityIdentifier(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool SequencesEqual(
        IReadOnlyList<string>? first,
        IReadOnlyList<string>? second)
    {
        if (first is null || second is null || first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            if (string.IsNullOrEmpty(first[index]) ||
                string.IsNullOrEmpty(second[index]) ||
                !string.Equals(first[index], second[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PathsEqual(string candidatePath, string configuredPath)
    {
        var normalizedCandidate = NormalizeAbsoluteLocalPath(candidatePath);
        var normalizedConfigured = NormalizeAbsoluteLocalPath(configuredPath);
        return normalizedCandidate is not null &&
               normalizedConfigured is not null &&
               string.Equals(
                   normalizedCandidate,
                   normalizedConfigured,
                   StringComparison.OrdinalIgnoreCase);
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
