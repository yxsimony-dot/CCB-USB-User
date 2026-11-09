using UsbWebLauncher.App.Composition;
using UsbWebLauncher.App.Presentation;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Tests.Composition;

public sealed class BankPinCredentialBindingTests
{
    [Fact]
    public async Task ReadStatus_UsesOnlyTheSelectedBankCredentialTarget()
    {
        var store = new RecordingPinStore();
        store.Seed(
            BankCredentialTargetResolver.PostalSavingsBankCredentialTarget,
            "synthetic-psbc-pin");
        var ccb = BankPinCredentialBinding.Create(
            BankAuthenticationRuntimeKind.ChinaConstructionBank,
            Authentication(
                BankCredentialTargetResolver.ChinaConstructionBankAuthenticationId,
                BankCredentialTargetResolver.ChinaConstructionBankCredentialTarget));
        var psbc = BankPinCredentialBinding.Create(
            BankAuthenticationRuntimeKind.PostalSavingsBank,
            Authentication(
                BankCredentialTargetResolver.PostalSavingsBankAuthenticationId,
                BankCredentialTargetResolver.PostalSavingsBankCredentialTarget));

        var ccbStatus = await ccb.ReadStatusAsync(store);
        var psbcStatus = await psbc.ReadStatusAsync(store);

        Assert.Equal(PinCredentialStatus.NotSet, ccbStatus);
        Assert.Equal(PinCredentialStatus.Set, psbcStatus);
        Assert.Equal(
            [
                BankCredentialTargetResolver.ChinaConstructionBankCredentialTarget,
                BankCredentialTargetResolver.PostalSavingsBankCredentialTarget
            ],
            store.ReadTargets);
    }

    [Fact]
    public async Task SelectedBankSetupAndDelete_DoNotModifyTheOtherBank()
    {
        var store = new RecordingPinStore();
        var stampStore = new RecordingStampStore();
        var ccbService = SetupService(
            Authentication(
                BankCredentialTargetResolver.ChinaConstructionBankAuthenticationId,
                BankCredentialTargetResolver.ChinaConstructionBankCredentialTarget),
            store,
            stampStore);
        var psbcService = SetupService(
            Authentication(
                BankCredentialTargetResolver.PostalSavingsBankAuthenticationId,
                BankCredentialTargetResolver.PostalSavingsBankCredentialTarget),
            store,
            stampStore);

        using (var pin = ClientCertificatePin.CopyOf("synthetic-ccb-pin".AsSpan()))
        {
            Assert.True(await ccbService.SaveFixedAsync(pin));
        }
        using (var pin = ClientCertificatePin.CopyOf("synthetic-psbc-pin".AsSpan()))
        {
            Assert.True(await psbcService.SaveFixedAsync(pin));
        }

        await psbcService.DeleteAsync();

        Assert.True(store.Contains(
            BankCredentialTargetResolver.ChinaConstructionBankCredentialTarget));
        Assert.False(store.Contains(
            BankCredentialTargetResolver.PostalSavingsBankCredentialTarget));
        Assert.DoesNotContain(
            BankCredentialTargetResolver.ChinaConstructionBankCredentialTarget,
            store.DeleteTargets);
    }

    [Fact]
    public void Create_RejectsCredentialTargetOwnedByAnotherBank()
    {
        var authentication = Authentication(
            BankCredentialTargetResolver.PostalSavingsBankAuthenticationId,
            BankCredentialTargetResolver.ChinaConstructionBankCredentialTarget);

        Assert.Throws<InvalidOperationException>(() =>
            BankPinCredentialBinding.Create(
                BankAuthenticationRuntimeKind.PostalSavingsBank,
                authentication));
    }

    private static ClientCertificatePinSetupService SetupService(
        ClientCertificateAuthenticationOptions authentication,
        IClientCertificatePinStore pinStore,
        IClientCertificateValidationStampStore stampStore) =>
        new(
            authentication,
            pinStore,
            stampStore,
            new UnusedUnlocker(),
            new UnusedVerifier());

    private static ClientCertificateAuthenticationOptions Authentication(
        string id,
        string targetName) =>
        new(
            id,
            "synthetic.example.test",
            443,
            new(
                "00112233445566778899AABBCCDDEEFF00112233",
                "CN=Synthetic",
                "CN=Synthetic Issuer"),
            new(
                "Synthetic Provider",
                1,
                "Synthetic Container",
                "AT_KEYEXCHANGE"),
            targetName,
            AuthenticationMode: "BrowserNative");

    private sealed class RecordingPinStore : IClientCertificatePinStore
    {
        private readonly Dictionary<string, char[]> _values =
            new(StringComparer.Ordinal);

        public List<string> ReadTargets { get; } = [];

        public List<string> DeleteTargets { get; } = [];

        public void Seed(string targetName, string value) =>
            _values[targetName] = value.ToCharArray();

        public bool Contains(string targetName) => _values.ContainsKey(targetName);

        public ValueTask<ClientCertificatePin?> ReadAsync(
            string targetName,
            CancellationToken cancellationToken = default)
        {
            ReadTargets.Add(targetName);
            return ValueTask.FromResult(
                _values.TryGetValue(targetName, out var value)
                    ? ClientCertificatePin.CopyOf(value.AsSpan())
                    : null);
        }

        public ValueTask SaveAsync(
            string targetName,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default)
        {
            _values[targetName] = pin.AsMemory().ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            string targetName,
            CancellationToken cancellationToken = default)
        {
            DeleteTargets.Add(targetName);
            if (_values.Remove(targetName, out var value))
            {
                Array.Clear(value);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingStampStore :
        IClientCertificateValidationStampStore
    {
        public ValueTask<ClientCertificateValidationStamp?> ReadAsync(
            string authenticationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ClientCertificateValidationStamp?>(null);

        public ValueTask SaveAsync(
            ClientCertificateValidationStamp stamp,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteAsync(
            string authenticationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class UnusedUnlocker : IClientCertificateTokenUnlocker
    {
        public ValueTask<ClientCertificateTokenUnlockResult> UnlockAsync(
            ClientCertificateAuthenticationOptions authentication,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Browser-native credential setup must not unlock the token.");
    }

    private sealed class UnusedVerifier : IClientCertificateSetupVerifier
    {
        public ValueTask<ClientCertificateSetupValidationResult> VerifyAsync(
            ClientCertificateAuthenticationOptions authentication,
            IClientCertificateTokenUnlockSession session,
            ClientCertificatePin pin,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Browser-native credential setup must not run TLS verification.");
    }
}
