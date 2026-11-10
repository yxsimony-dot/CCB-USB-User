using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.Tests.Devices;

public sealed class PsbcUsbKeyVendorProbeTests
{
    public static TheoryData<
        IPsbcUsbKeyProbe,
        UsbDeviceDescriptor,
        UsbKeyVendorFamily> VendorFixtures => new()
    {
        {
            new FeitianEnterSafePsbcUsbKeyProbe(),
            Device(
                @"USB\VID_096E&PID_0005\FEITIAN",
                "Token EasyPlay"),
            UsbKeyVendorFamily.FeitianEnterSafe
        },
        {
            new HdzbPsbcUsbKeyProbe(),
            Device(
                @"USB\VID_1677&PID_6004\HDZB",
                "PSBC_HDZB_USBKey2G"),
            UsbKeyVendorFamily.Hdzb
        },
        {
            new TendyronPsbcUsbKeyProbe(),
            Device(
                @"USB\VID_1780&PID_0401\TENDYRON",
                "Tendyron Token",
                ["TENDYRON U-DISK"],
                ["PSBC"]),
            UsbKeyVendorFamily.Tendyron
        },
        {
            new ExcelsecuPsbcUsbKeyProbe(),
            Device(
                @"USB\VID_1EA8&PID_C03E\EXCELSECU",
                "prod_composite_token ven_swukey"),
            UsbKeyVendorFamily.Excelsecu
        },
        {
            new WatchDataPsbcUsbKeyProbe(),
            Device(
                @"USB\VID_163C&PID_0620\WATCHDATA",
                "WatchData USB Key"),
            UsbKeyVendorFamily.WatchData
        }
    };

    [Theory]
    [MemberData(nameof(VendorFixtures))]
    public async Task ProbeAsync_ReturnsAbsentWithoutPhysicalCandidate(
        IPsbcUsbKeyProbe probe,
        UsbDeviceDescriptor candidate,
        UsbKeyVendorFamily vendorFamily)
    {
        var result = await probe.ProbeAsync(
            [],
            ProbeEnvironment.Ready());

        Assert.Equal(UsbKeyDetectionStatus.Absent, result.Status);
        Assert.Equal(vendorFamily, result.VendorFamily);
        Assert.Null(result.PhysicalInstanceId);
        Assert.NotEqual(candidate.InstanceId, result.PhysicalInstanceId);
    }

    [Theory]
    [MemberData(nameof(VendorFixtures))]
    public async Task ProbeAsync_ReturnsEnvironmentUnavailableWhenProviderIsMissing(
        IPsbcUsbKeyProbe probe,
        UsbDeviceDescriptor candidate,
        UsbKeyVendorFamily vendorFamily)
    {
        var environment = ProbeEnvironment.Ready() with
        {
            ProviderRegistered = false
        };

        var result = await probe.ProbeAsync([candidate], environment);

        Assert.Equal(
            UsbKeyDetectionStatus.PresentButEnvironmentUnavailable,
            result.Status);
        Assert.Equal(vendorFamily, result.VendorFamily);
        Assert.Equal(
            UsbKeyDiagnosticCode.ProviderUnavailable,
            result.DiagnosticCode);
        Assert.Equal(candidate.InstanceId, result.PhysicalInstanceId);
    }

    [Theory]
    [MemberData(nameof(VendorFixtures))]
    public async Task ProbeAsync_ReturnsReadyWithCurrentTokenEvidence(
        IPsbcUsbKeyProbe probe,
        UsbDeviceDescriptor candidate,
        UsbKeyVendorFamily vendorFamily)
    {
        var result = await probe.ProbeAsync(
            [candidate],
            ProbeEnvironment.Ready("fixture-token"));

        Assert.Equal(UsbKeyDetectionStatus.Ready, result.Status);
        Assert.Equal(vendorFamily, result.VendorFamily);
        Assert.Equal(candidate.InstanceId, result.PhysicalInstanceId);
        Assert.Equal("fixture-token", result.TokenIdentity);
    }

    [Fact]
    public async Task Tendyron_PreservesAllContainerEvidenceAsASnapshot()
    {
        var containers = new List<string> { "EXCHANGE", "SIGNING", @"\\.\Reader\SIGNING" };
        var environment = ProbeEnvironment.Ready() with
        {
            TokenEvidence = new(true, "container-set-binding", containers)
        };

        var result = await new TendyronPsbcUsbKeyProbe().ProbeAsync(
            [Device(@"USB\VID_1780&PID_0401\SYNTHETIC", "Tendyron Token")],
            environment);
        containers.Clear();

        Assert.Equal("container-set-binding", result.TokenIdentity);
        Assert.Equal(
            ["EXCHANGE", "SIGNING", @"\\.\Reader\SIGNING"],
            result.ContainerIdentities);
    }

    [Fact]
    public async Task Tendyron_RejectsAmbiguousPhysicalKeysBeforeProviderEnumeration()
    {
        var environment = ProbeEnvironment.Ready("container");

        var result = await new TendyronPsbcUsbKeyProbe().ProbeAsync(
            [
                Device(@"USB\VID_1780&PID_0401\FIRST", "Tendyron Token"),
                Device(@"USB\VID_1780&PID_0401\SECOND", "Tendyron Token")
            ],
            environment);

        Assert.Equal(UsbKeyDetectionStatus.PresentButEnvironmentUnavailable, result.Status);
        Assert.Equal(UsbKeyDiagnosticCode.TokenUnavailable, result.DiagnosticCode);
        Assert.Equal(0, environment.TokenReadCount);
    }

    [Fact]
    public async Task Tendyron_DuplicateDescriptorsOfSameDeviceAreNotAmbiguous()
    {
        var device = Device(@"USB\VID_1780&PID_0401\SYNTHETIC", "Tendyron Token");

        var result = await new TendyronPsbcUsbKeyProbe().ProbeAsync(
            [device, device], ProbeEnvironment.Ready("container"));

        Assert.Equal(UsbKeyDetectionStatus.Ready, result.Status);
    }

    [Fact]
    public async Task Tendyron_CurrentCapturedFixtureReturnsReady()
    {
        var fixture = Device(
            @"USB\VID_1780&PID_0401\CURRENT-PSBC-KEY",
            "Tendyron Token",
            ["TENDYRON U-DISK USB Device"],
            ["PSBC"]);

        var result = await new TendyronPsbcUsbKeyProbe().ProbeAsync(
            [fixture],
            ProbeEnvironment.Ready("sanitized-current-token"));

        Assert.Equal(UsbKeyDetectionStatus.Ready, result.Status);
        Assert.Equal(UsbKeyVendorFamily.Tendyron, result.VendorFamily);
        Assert.Equal(fixture.InstanceId, result.PhysicalInstanceId);
    }

    [Fact]
    public async Task Hdzb_ReportsStoppedServiceBeforeProviderChecks()
    {
        var environment = ProbeEnvironment.Ready() with
        {
            ServiceState = PsbcServiceState.Stopped
        };
        var fixture = Device(
            @"USB\VID_1677&PID_6004\HDZB",
            "PSBC_HDZB_USBKey2G");

        var result = await new HdzbPsbcUsbKeyProbe().ProbeAsync(
            [fixture],
            environment);

        Assert.Equal(
            UsbKeyDiagnosticCode.ServiceUnavailable,
            result.DiagnosticCode);
        Assert.Equal(0, environment.ProviderReadCount);
    }

    [Fact]
    public async Task Hdzb_RequiresItsRegistryMarker()
    {
        var environment = ProbeEnvironment.Ready() with
        {
            RegistryMarkerPresent = false
        };
        var fixture = Device(
            @"USB\VID_1677&PID_6004\HDZB",
            "PSBC_HDZB_USBKey2G");

        var result = await new HdzbPsbcUsbKeyProbe().ProbeAsync(
            [fixture],
            environment);

        Assert.Equal(
            UsbKeyDiagnosticCode.RegistryMarkerUnavailable,
            result.DiagnosticCode);
    }

    [Fact]
    public async Task Tendyron_DoesNotCombineMediaEvidenceAcrossDevices()
    {
        var hardwareOnly = Device(
            @"USB\VID_9999&PID_9999\TARGET",
            "Generic USB Key",
            ["TENDYRON U-DISK"],
            []);
        var labelOnly = Device(
            @"USB\VID_8888&PID_8888\OTHER",
            "Generic storage",
            [],
            ["PSBC"]);

        var result = await new TendyronPsbcUsbKeyProbe().ProbeAsync(
            [hardwareOnly, labelOnly],
            ProbeEnvironment.Ready());

        Assert.Equal(UsbKeyDetectionStatus.Absent, result.Status);
    }

    [Fact]
    public async Task InstalledEnvironmentWithoutCandidateNeverReturnsReady()
    {
        var environment = ProbeEnvironment.Ready("installed-token");

        var results = await new PsbcUsbKeyProbeAggregator(
                PsbcUsbKeyVendorProbes.All,
                environment,
                new PsbcUsbKeyProbeExecutor(),
                TimeSpan.FromSeconds(1))
            .DetectAsync([]);

        Assert.Empty(results);
        Assert.Equal(0, environment.TokenReadCount);
    }

    [Fact]
    public async Task RepeatedRefreshUsesOnlyReadOnlyEnvironmentOperations()
    {
        var environment = ProbeEnvironment.Ready();
        var fixture = Device(
            @"USB\VID_1780&PID_0401\TENDYRON",
            "Tendyron Token",
            ["TENDYRON U-DISK"],
            ["PSBC"]);
        var probe = new TendyronPsbcUsbKeyProbe();

        await probe.ProbeAsync([fixture], environment);
        await probe.ProbeAsync([fixture], environment);

        Assert.Equal(2, environment.ServiceReadCount);
        Assert.Equal(2, environment.ProviderReadCount);
        Assert.Equal(2, environment.TokenReadCount);
        Assert.DoesNotContain(
            typeof(IPsbcUsbKeyEnvironmentQuery).GetMethods(),
            method => new[]
            {
                "Initialize",
                "TokenInitialize",
                "Pin",
                "Sign",
                "Format",
                "Unlock"
            }.Any(forbidden => method.Name.Contains(
                forbidden,
                StringComparison.OrdinalIgnoreCase)));
    }

    private static UsbDeviceDescriptor Device(
        string instanceId,
        string description,
        IReadOnlyList<string>? cdRomNames = null,
        IReadOnlyList<string>? volumeLabels = null)
    {
        var hardwareId = instanceId[..instanceId.LastIndexOf('\\')];
        return new UsbDeviceDescriptor(
            instanceId,
            [hardwareId],
            description,
            cdRomNames ?? [],
            volumeLabels ?? [],
            []);
    }

    private sealed record ProbeEnvironment(
        PsbcServiceState ServiceState,
        bool ProviderRegistered,
        bool RegistryMarkerPresent,
        PsbcTokenEvidence TokenEvidence) : IPsbcUsbKeyEnvironmentQuery
    {
        public int ServiceReadCount { get; private set; }
        public int ProviderReadCount { get; private set; }
        public int RegistryReadCount { get; private set; }
        public int TokenReadCount { get; private set; }

        public static ProbeEnvironment Ready(string? tokenIdentity = null) =>
            new(
                PsbcServiceState.Running,
                ProviderRegistered: true,
                RegistryMarkerPresent: true,
                new PsbcTokenEvidence(true, tokenIdentity));

        public Task<PsbcServiceState> GetServiceStateAsync(
            string serviceName,
            CancellationToken cancellationToken = default)
        {
            ServiceReadCount++;
            return Task.FromResult(ServiceState);
        }

        public Task<bool> IsCryptographicProviderRegisteredAsync(
            string providerName,
            CancellationToken cancellationToken = default)
        {
            ProviderReadCount++;
            return Task.FromResult(ProviderRegistered);
        }

        public Task<bool> IsRegistryMarkerPresentAsync(
            PsbcRegistryMarker marker,
            CancellationToken cancellationToken = default)
        {
            RegistryReadCount++;
            return Task.FromResult(RegistryMarkerPresent);
        }

        public Task<PsbcTokenEvidence> QueryTokenAsync(
            PsbcTokenQuery query,
            CancellationToken cancellationToken = default)
        {
            TokenReadCount++;
            return Task.FromResult(TokenEvidence);
        }
    }
}
