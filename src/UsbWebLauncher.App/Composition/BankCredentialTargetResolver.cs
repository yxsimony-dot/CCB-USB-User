namespace UsbWebLauncher.App.Composition;

internal static class BankCredentialTargetResolver
{
    internal const string ChinaConstructionBankAuthenticationId =
        "ccb-client-certificate";
    internal const string PostalSavingsBankAuthenticationId =
        "psbc-client-certificate";

    internal const string ChinaConstructionBankCredentialTarget =
        "UsbWebLauncher/ClientCertificate/ccb-client-certificate";
    internal const string PostalSavingsBankCredentialTarget =
        "UsbWebLauncher/ClientCertificate/psbc-client-certificate";

    public static string Resolve(
        BankAuthenticationRuntimeKind runtimeKind,
        string authenticationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationId);

        var (expectedAuthenticationId, credentialTarget) = runtimeKind switch
        {
            BankAuthenticationRuntimeKind.ChinaConstructionBank => (
                ChinaConstructionBankAuthenticationId,
                ChinaConstructionBankCredentialTarget),
            BankAuthenticationRuntimeKind.PostalSavingsBank => (
                PostalSavingsBankAuthenticationId,
                PostalSavingsBankCredentialTarget),
            _ => throw new ArgumentOutOfRangeException(
                nameof(runtimeKind),
                runtimeKind,
                "Unsupported bank authentication runtime.")
        };

        if (!string.Equals(
                authenticationId,
                expectedAuthenticationId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "认证配置 ID 与当前银行运行时不匹配。");
        }

        return credentialTarget;
    }
}
