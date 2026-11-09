using UsbWebLauncher.App.Configuration;
using UsbWebLauncher.Core.Configuration;
using UsbWebLauncher.Core.Devices;

namespace UsbWebLauncher.App.Composition;

internal interface IBankUsbKeyFlow
{
    Func<UsbDeviceDescriptor, bool> CoordinatorMatcher { get; }

    IUsbKeyPresenceObserver CreatePresenceObserver(
        Action<UsbKeyDetectionStatus?> publish);

    IUsbDeviceMonitor CreateSessionMonitor(
        Action onDeviceRemoved,
        BankRuntimeCallbackGuard? runtimeGuard = null);
}

internal interface IBankUsbKeyFlowBranchFactory
{
    BankFlowKind Kind { get; }

    IBankUsbKeyFlow Create(LauncherProfile profile);
}

internal sealed class BankUsbKeyFlowFactory(
    IEnumerable<IBankUsbKeyFlowBranchFactory> branches)
{
    private readonly IReadOnlyDictionary<BankFlowKind, IBankUsbKeyFlowBranchFactory> branches =
        branches?.ToDictionary(branch => branch.Kind) ??
        throw new ArgumentNullException(nameof(branches));

    public IBankUsbKeyFlow Create(
        BankFlowKind kind,
        LauncherProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!branches.TryGetValue(kind, out var branch))
        {
            throw new InvalidOperationException(
                $"No USB Key flow is configured for bank '{kind}'.");
        }

        return branch.Create(profile);
    }

    public static BankUsbKeyFlowFactory CreateDefault(
        Func<IUsbMetadataRetryPolicy, IUsbDeviceMonitor> monitorFactory,
        Func<IPsbcUsbKeyDetector> psbcDetectorFactory) =>
        new(
        [
            new CcbBankUsbKeyFlowBranchFactory(monitorFactory),
            new PsbcBankUsbKeyFlowBranchFactory(
                monitorFactory,
                psbcDetectorFactory)
        ]);
}

internal sealed class CcbBankUsbKeyFlowBranchFactory(
    Func<IUsbMetadataRetryPolicy, IUsbDeviceMonitor> monitorFactory)
    : IBankUsbKeyFlowBranchFactory
{
    public BankFlowKind Kind => BankFlowKind.ChinaConstructionBank;

    public IBankUsbKeyFlow Create(LauncherProfile profile) =>
        new CcbBankUsbKeyFlow(profile, monitorFactory);
}

internal sealed class PsbcBankUsbKeyFlowBranchFactory(
    Func<IUsbMetadataRetryPolicy, IUsbDeviceMonitor> monitorFactory,
    Func<IPsbcUsbKeyDetector> detectorFactory)
    : IBankUsbKeyFlowBranchFactory
{
    public BankFlowKind Kind => BankFlowKind.PostalSavingsBank;

    public IBankUsbKeyFlow Create(LauncherProfile profile) =>
        new PsbcBankUsbKeyFlow(profile, monitorFactory, detectorFactory);
}

internal sealed class CcbBankUsbKeyFlow(
    LauncherProfile profile,
    Func<IUsbMetadataRetryPolicy, IUsbDeviceMonitor> monitorFactory)
    : IBankUsbKeyFlow
{
    public Func<UsbDeviceDescriptor, bool> CoordinatorMatcher { get; } =
        device => UsbDeviceMatcher.IsMatch(device, profile);

    public IUsbKeyPresenceObserver CreatePresenceObserver(
        Action<UsbKeyDetectionStatus?> publish)
    {
        var monitor = monitorFactory(CcbUsbMetadataRetryPolicy.Instance);
        return new UsbKeyPresenceObserver(
            monitor,
            CoordinatorMatcher,
            present => publish(present switch
            {
                true => UsbKeyDetectionStatus.Ready,
                false => UsbKeyDetectionStatus.Absent,
                null => null
            }));
    }

    public IUsbDeviceMonitor CreateSessionMonitor(
        Action onDeviceRemoved,
        BankRuntimeCallbackGuard? runtimeGuard = null)
    {
        var monitor = monitorFactory(CcbUsbMetadataRetryPolicy.Instance);
        return new SessionUsbDeviceMonitor(
            monitor,
            CoordinatorMatcher,
            onDeviceRemoved);
    }
}

internal sealed class PsbcBankUsbKeyFlow(
    LauncherProfile profile,
    Func<IUsbMetadataRetryPolicy, IUsbDeviceMonitor> monitorFactory,
    Func<IPsbcUsbKeyDetector> detectorFactory)
    : IBankUsbKeyFlow
{
    public Func<UsbDeviceDescriptor, bool> CoordinatorMatcher { get; } =
        PsbcUsbKeyMatcher.IsCandidate;

    public IUsbKeyPresenceObserver CreatePresenceObserver(
        Action<UsbKeyDetectionStatus?> publish)
    {
        EnsurePsbcProfile();
        var monitor = new PsbcUsbKeyDeviceMonitor(
            monitorFactory(PsbcUsbMetadataRetryPolicy.Instance),
            detectorFactory());
        return new PsbcUsbKeyPresenceObserver(monitor, publish);
    }

    public IUsbDeviceMonitor CreateSessionMonitor(
        Action onDeviceRemoved,
        BankRuntimeCallbackGuard? runtimeGuard = null)
    {
        EnsurePsbcProfile();
        IUsbDeviceMonitor monitor = monitorFactory(
            PsbcUsbMetadataRetryPolicy.Instance);
        monitor = new SessionUsbDeviceMonitor(
            monitor,
            PsbcUsbKeyMatcher.IsCandidate,
            onDeviceRemoved);
        return new PsbcUsbKeyDeviceMonitor(
            monitor,
            detectorFactory(),
            runtimeGuard);
    }

    private void EnsurePsbcProfile()
    {
        if (!string.Equals(
                profile.UsbDetectionStrategy,
                "psbcUsbKey",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Postal Savings Bank flow requires the psbcUsbKey strategy.");
        }
    }
}
