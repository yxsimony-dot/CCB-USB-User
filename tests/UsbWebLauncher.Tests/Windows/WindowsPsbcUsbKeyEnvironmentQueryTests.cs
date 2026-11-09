using System.Text;
using UsbWebLauncher.Core.Devices;
using UsbWebLauncher.Windows.Devices;

namespace UsbWebLauncher.Tests.Windows;

public sealed class WindowsPsbcUsbKeyEnvironmentQueryTests
{
    [Fact]
    public async Task Query_DelegatesEveryEnvironmentReadToInjectableReaders()
    {
        var service = new StubServiceReader(PsbcServiceState.Running);
        var provider = new StubProviderReader(
            registered: true,
            providerType: 1);
        var registry = new StubRegistryReader(present: true);
        var token = new StubTokenReader(new PsbcTokenEvidence(true, "TOKEN-IDENTITY"));
        var query = new WindowsPsbcUsbKeyEnvironmentQuery(
            service,
            provider,
            registry,
            token);
        var marker = new PsbcRegistryMarker(@"HKLM\Software\PSBC");
        var tokenQuery = new PsbcTokenQuery(
            UsbKeyVendorFamily.Tendyron,
            "USB\\VID_1780&PID_0401\\INSTANCE");

        Assert.Equal(
            PsbcServiceState.Running,
            await query.GetServiceStateAsync("OnKey Service _PSBC"));
        Assert.True(await query.IsCryptographicProviderRegisteredAsync(
            "Tendyron OnKey193 CSP PSBC v1.0"));
        Assert.True(await query.IsRegistryMarkerPresentAsync(marker));
        Assert.Equal(
            new PsbcTokenEvidence(true, "TOKEN-IDENTITY"),
            await query.QueryTokenAsync(tokenQuery));

        Assert.Equal("OnKey Service _PSBC", service.LastServiceName);
        Assert.Equal("Tendyron OnKey193 CSP PSBC v1.0", provider.LastProviderName);
        Assert.Same(marker, registry.LastMarker);
        Assert.Same(tokenQuery, token.LastQuery);
    }

    [Fact]
    public async Task Query_HonorsCancellationBeforeStartingARead()
    {
        var query = new WindowsPsbcUsbKeyEnvironmentQuery(
            new StubServiceReader(PsbcServiceState.Running),
            new StubProviderReader(true, 1),
            new StubRegistryReader(true),
            new StubTokenReader(new PsbcTokenEvidence(true)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            query.GetServiceStateAsync("service", cancellation.Token));
    }

    [Fact]
    public void EnvironmentContracts_ExposeOnlyReadOperations()
    {
        var publicMethods = new[]
            {
                typeof(IPsbcUsbKeyEnvironmentQuery),
                typeof(WindowsPsbcUsbKeyEnvironmentQuery)
            }
            .SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.DeclaredOnly))
            .Select(method => method.Name)
            .ToArray();

        Assert.NotEmpty(publicMethods);
        Assert.DoesNotContain(publicMethods, name =>
            new[] { "Initialize", "Pin", "Sign", "Format", "Unlock", "Write", "Set", "Delete" }
                .Any(forbidden => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void CspTokenReader_ReturnsCurrentContainerWithoutMutationCalls()
    {
        var provider = new StubProviderReader(
            registered: true,
            providerType: 1);
        var native = new StubCryptoProviderProbeNative("PSBC-CONTAINER");
        var reader = new WindowsCspTokenEvidenceReader(provider, native);

        var result = reader.Query(new PsbcTokenQuery(
            UsbKeyVendorFamily.Tendyron,
            "USB\\VID_1780&PID_0401\\INSTANCE"));

        Assert.True(result.IsPresent);
        Assert.Equal("PSBC-CONTAINER", result.TokenIdentity);
        Assert.Equal(1, native.AcquireCount);
        Assert.Equal(2, native.ReadCount);
        Assert.Equal(1, native.ReleaseCount);
    }

    [Fact]
    public void CspTokenReader_DoesNotTreatInstalledProviderAsConnectedToken()
    {
        var provider = new StubProviderReader(
            registered: true,
            providerType: 1);
        var native = new StubCryptoProviderProbeNative(containerIdentity: null);
        var reader = new WindowsCspTokenEvidenceReader(provider, native);

        var result = reader.Query(new PsbcTokenQuery(
            UsbKeyVendorFamily.Tendyron,
            "USB\\VID_1780&PID_0401\\INSTANCE"));

        Assert.False(result.IsPresent);
        Assert.Null(result.TokenIdentity);
        Assert.Equal(1, native.ReleaseCount);
    }

    private sealed class StubServiceReader(PsbcServiceState state)
        : IWindowsServiceStateReader
    {
        public string? LastServiceName { get; private set; }

        public PsbcServiceState GetServiceState(string serviceName)
        {
            LastServiceName = serviceName;
            return state;
        }
    }

    private sealed class StubProviderReader(bool registered, uint providerType)
        : IWindowsCryptographicProviderReader
    {
        public string? LastProviderName { get; private set; }

        public bool IsRegistered(string providerName)
        {
            LastProviderName = providerName;
            return registered;
        }

        public bool TryGetProviderType(string providerName, out uint resolvedProviderType)
        {
            LastProviderName = providerName;
            resolvedProviderType = providerType;
            return registered;
        }
    }

    private sealed class StubRegistryReader(bool present)
        : IWindowsRegistryMarkerReader
    {
        public PsbcRegistryMarker? LastMarker { get; private set; }

        public bool IsPresent(PsbcRegistryMarker marker)
        {
            LastMarker = marker;
            return present;
        }
    }

    private sealed class StubTokenReader(PsbcTokenEvidence evidence)
        : IWindowsPsbcTokenEvidenceReader
    {
        public PsbcTokenQuery? LastQuery { get; private set; }

        public PsbcTokenEvidence Query(PsbcTokenQuery query)
        {
            LastQuery = query;
            return evidence;
        }
    }

    private sealed class StubCryptoProviderProbeNative(string? containerIdentity)
        : IWindowsCryptoProviderProbeNative
    {
        public int AcquireCount { get; private set; }
        public int ReadCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public bool Acquire(
            out IntPtr provider,
            string? containerName,
            string providerName,
            uint providerType,
            uint flags)
        {
            AcquireCount++;
            provider = new IntPtr(1);
            return true;
        }

        public bool GetProviderParameter(
            IntPtr provider,
            uint parameter,
            byte[]? data,
            ref uint dataLength,
            uint flags)
        {
            ReadCount++;
            if (containerIdentity is null)
            {
                dataLength = 0;
                return false;
            }

            var encoded = Encoding.Default.GetBytes(containerIdentity + "\0");
            dataLength = checked((uint)encoded.Length);
            if (data is not null)
            {
                encoded.CopyTo(data, 0);
            }

            return true;
        }

        public bool Release(IntPtr provider, uint flags)
        {
            ReleaseCount++;
            return true;
        }
    }
}
