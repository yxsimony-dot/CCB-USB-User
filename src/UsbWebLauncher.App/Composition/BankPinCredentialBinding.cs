using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.App.Composition;

internal sealed class BankPinCredentialBinding
{
    private BankPinCredentialBinding(
        BankAuthenticationRuntimeKind runtimeKind,
        string authenticationId,
        string credentialTargetName)
    {
        RuntimeKind = runtimeKind;
        AuthenticationId = authenticationId;
        CredentialTargetName = credentialTargetName;
    }

    public BankAuthenticationRuntimeKind RuntimeKind { get; }

    public string AuthenticationId { get; }

    public string CredentialTargetName { get; }

    public static BankPinCredentialBinding Create(
        BankAuthenticationRuntimeKind runtimeKind,
        ClientCertificateAuthenticationOptions authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        var targetName = BankCredentialTargetResolver.Resolve(
            runtimeKind,
            authentication.Id);
        if (!string.Equals(
                targetName,
                authentication.CredentialTargetName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PIN 界面的凭据目标与当前银行运行时不一致。");
        }

        return new(runtimeKind, authentication.Id, targetName);
    }

    public async ValueTask<PinCredentialStatus> ReadStatusAsync(
        IClientCertificatePinStore pinStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pinStore);
        using var pin = await pinStore.ReadAsync(
                CredentialTargetName,
                cancellationToken)
            .ConfigureAwait(false);
        return pin is null
            ? PinCredentialStatus.NotSet
            : PinCredentialStatus.Set;
    }
}
