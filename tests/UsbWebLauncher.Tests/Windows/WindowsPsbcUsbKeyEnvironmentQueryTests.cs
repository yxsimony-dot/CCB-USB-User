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
        var reader = new WindowsCspTokenEvidenceReader(provider, native, (_, _) => []);

        var result = reader.Query(new PsbcTokenQuery(
            UsbKeyVendorFamily.Tendyron,
            "USB\\VID_1780&PID_0401\\INSTANCE"));

        Assert.True(result.IsPresent);
        Assert.Equal("PSBC-CONTAINER", result.TokenIdentity);
        Assert.Equal(["PSBC-CONTAINER"], result.ContainerIdentities);
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
        var native = new StubCryptoProviderProbeNative();
        var reader = new WindowsCspTokenEvidenceReader(provider, native, (_, _) => []);

        var result = reader.Query(new PsbcTokenQuery(
            UsbKeyVendorFamily.Tendyron,
            "USB\\VID_1780&PID_0401\\INSTANCE"));

        Assert.False(result.IsPresent);
        Assert.Null(result.TokenIdentity);
        Assert.Equal(1, native.ReleaseCount);
    }

    [Fact]
    public void CspTokenReader_EnumeratesAllContainersAndVerifiesReaderQualifiedAlias()
    {
        const string qualified = @"\\.\Synthetic Reader\SIGNING";
        var native = new StubCryptoProviderProbeNative("EXCHANGE", "SIGNING");
        var result = ReadToken(native, [qualified]);

        Assert.True(result.IsPresent);
        Assert.Equal(["EXCHANGE", "SIGNING", qualified], result.ContainerIdentities);
        Assert.StartsWith("csp-containers:", result.TokenIdentity);
        Assert.Equal(2, native.AcquireCount);
        Assert.Equal(2, native.ReleaseCount);
        Assert.Equal(new uint[] { 1, 0, 0 }, native.EnumerationFlags);
        Assert.All(native.AcquireFlags, flags => Assert.Equal(0xF0000040u, flags));
    }

    [Theory]
    [InlineData(false, "SIGNING")]
    [InlineData(true, "OTHER")]
    public void CspTokenReader_RejectsAliasWhenDriverCannotVerifyIt(bool acquireSucceeds, string resolvedName)
    {
        const string qualified = @"\\.\Synthetic Reader\SIGNING";
        var native = new StubCryptoProviderProbeNative("EXCHANGE", "SIGNING")
        {
            AliasAcquireSucceeds = acquireSucceeds,
            ResolvedAliasName = resolvedName
        };

        var result = ReadToken(native, [qualified]);

        Assert.DoesNotContain(qualified, result.ContainerIdentities!);
        Assert.Equal(["EXCHANGE", "SIGNING"], result.ContainerIdentities);
        Assert.Equal(acquireSucceeds ? 2 : 1, native.ReleaseCount);
    }

    [Theory]
    [InlineData(@"\\.\Other Reader\UNENUMERATED")]
    [InlineData(@"\\.\Reader\folder\SIGNING")]
    [InlineData(@"\\.\Reader\")]
    [InlineData(@"\\.\SIGNING")]
    [InlineData(@"\\.\\SIGNING")]
    [InlineData(@"Other Reader\SIGNING")]
    public void CspTokenReader_DoesNotOpenUnrelatedOrMalformedAliases(string qualified)
    {
        var native = new StubCryptoProviderProbeNative("SIGNING");

        var result = ReadToken(native, [qualified]);

        Assert.Equal(["SIGNING"], result.ContainerIdentities);
        Assert.Equal(1, native.AcquireCount);
    }

    [Fact]
    public void CspTokenReader_IdentityIsOrderIndependentAndChangesWhenAnyContainerChanges()
    {
        var original = ReadToken(new("FIRST", "SECOND"));
        var reordered = ReadToken(new("SECOND", "FIRST"));
        var changed = ReadToken(new("FIRST", "THIRD"));
        var aliasChanged = ReadToken(new("FIRST", "SECOND"), [@"\\.\Reader\SECOND"]);

        Assert.Equal(original.TokenIdentity, reordered.TokenIdentity);
        Assert.NotEqual(original.TokenIdentity, changed.TokenIdentity);
        Assert.NotEqual(original.TokenIdentity, aliasChanged.TokenIdentity);
    }

    [Fact]
    public void CspTokenReader_DiscardsPartialEnumerationOnNativeError()
    {
        var native = new StubCryptoProviderProbeNative("FIRST") { EnumerationError = 5 };

        var result = ReadToken(native);

        Assert.False(result.IsPresent);
        Assert.Null(result.ContainerIdentities);
        Assert.Equal(1, native.ReleaseCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("FIRST")]
    public void CspTokenReader_RejectsEmptyOrRepeatedContainer(string second)
    {
        var native = new StubCryptoProviderProbeNative("FIRST", second);

        var result = ReadToken(native);

        Assert.False(result.IsPresent);
        Assert.Equal(1, native.ReleaseCount);
    }

    [Fact]
    public void CspTokenReader_BoundsEnumerationAndReleasesProvider()
    {
        var native = new StubCryptoProviderProbeNative(
            Enumerable.Range(0, 65).Select(index => $"CONTAINER-{index}").ToArray());

        var result = ReadToken(native);

        Assert.False(result.IsPresent);
        Assert.Equal(65, native.ReadCount);
        Assert.Equal(1, native.ReleaseCount);
    }

    private static PsbcTokenEvidence ReadToken(
        StubCryptoProviderProbeNative native,
        IReadOnlyList<string>? aliases = null) =>
        new WindowsCspTokenEvidenceReader(
                new StubProviderReader(true, 1), native, (_, _) => aliases ?? [])
            .Query(new(UsbKeyVendorFamily.Tendyron, @"USB\VID_1780&PID_0401\SYNTHETIC"));

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

    private sealed class StubCryptoProviderProbeNative(params string[] containers)
        : IWindowsCryptoProviderProbeNative
    {
        private int containerIndex;
        private string? alias;
        public int AcquireCount { get; private set; }
        public int ReadCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public List<uint> EnumerationFlags { get; } = [];
        public List<uint> AcquireFlags { get; } = [];
        public bool AliasAcquireSucceeds { get; init; } = true;
        public string? ResolvedAliasName { get; init; }
        public int EnumerationError { get; init; } = 259;

        public bool Acquire(
            out IntPtr provider,
            string? containerName,
            string providerName,
            uint providerType,
            uint flags)
        {
            AcquireCount++;
            AcquireFlags.Add(flags);
            alias = containerName;
            provider = containerName is null
                ? new IntPtr(1)
                : AliasAcquireSucceeds ? new IntPtr(2) : IntPtr.Zero;
            return provider != IntPtr.Zero;
        }

        public bool GetProviderParameter(
            IntPtr provider,
            uint parameter,
            byte[]? data,
            ref uint dataLength,
            uint flags)
        {
            ReadCount++;
            string name;
            if (parameter == 2)
            {
                EnumerationFlags.Add(flags);
                if (flags == 1)
                {
                    containerIndex = 0;
                }
                if (containerIndex >= containers.Length)
                {
                    dataLength = 0;
                    return false;
                }
                name = containers[containerIndex++];
            }
            else
            {
                Assert.Equal(6u, parameter);
                Assert.Equal(new IntPtr(2), provider);
                name = ResolvedAliasName ?? alias!.Split('\\')[^1];
            }

            var encoded = Encoding.ASCII.GetBytes(name + "\0");
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

        public int GetLastError() => EnumerationError;
    }
}
