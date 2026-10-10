using UsbWebLauncher.Core.Devices;
using UsbWebLauncher.Windows.Devices;

namespace UsbWebLauncher.Tests.Windows;

public sealed class WindowsUsbDeviceMonitorTests
{
    [Fact]
    public async Task CcbRemoval_ReportsFreshAbsenceBeforeRetryingUnrelatedStorage()
    {
        var enumerator = new SequenceEnumerator([CcbDevice()], [PendingDevice("USB\\OTHER")]);
        var source = new RecordingChangeSource();
        await using var monitor = CreateCcbMonitor(enumerator, source, retryDelay: TimeSpan.FromMinutes(1));
        await monitor.StartAsync();
        Assert.Single(await monitor.GetConnectedDevicesAsync());
        var absent = new TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += (_, args) => { absent.TrySetResult(args.Devices); return Task.CompletedTask; };

        source.RaiseChanged();
        var snapshot = await absent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(Assert.Single(snapshot).CdRomNames);
        Assert.Equal(2, enumerator.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CcbRemoval_PhysicalRemovalRetryPublishesOnlyNewPresenceAndDoesNotRepeatAbsence(bool metadataReturns)
    {
        var enumerator = new SequenceEnumerator([CcbDevice()], [PendingDevice("USB\\OTHER")], metadataReturns ? [CcbDevice()] : []);
        var source = new RecordingChangeSource();
        await using var monitor = CreateCcbMonitor(enumerator, source);
        await monitor.StartAsync();
        Assert.Single(await monitor.GetConnectedDevicesAsync());
        var publications = new List<bool>();
        monitor.DevicesChanged += (_, args) =>
        {
            publications.Add(args.Devices.Any(device => device.CdRomNames.Contains("CCB USB CD")));
            return Task.CompletedTask;
        };

        source.RaiseChanged();
        await enumerator.ThirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        await monitor.DisposeAsync();

        Assert.Equal(metadataReturns ? new[] { false, true } : new[] { false }, publications);
    }

    [Theory]
    [InlineData("USB\\KEY")]
    [InlineData("usb\\key")]
    public async Task CcbMetadataLoss_SamePhysicalNodeRecoversWithoutTransientAbsence(string instanceId)
    {
        var enumerator = new SequenceEnumerator([CcbDevice()], [PendingDevice(instanceId)], [CcbDevice()]);
        var source = new RecordingChangeSource();
        await using var monitor = CreateCcbMonitor(enumerator, source);
        await monitor.StartAsync();
        Assert.Single(await monitor.GetConnectedDevicesAsync());
        var publications = new List<bool>();
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += (_, args) =>
        {
            var matches = args.Devices.Any(device => device.CdRomNames.Contains("CCB USB CD"));
            publications.Add(matches);
            if (matches) restored.TrySetResult();
            return Task.CompletedTask;
        };

        source.RaiseChanged();
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { true }, publications);
        Assert.Equal(3, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbMetadataLoss_SameNodeExhaustionReportsFreshAbsenceOnlyAfterRetryLimit()
    {
        var enumerator = new SequenceEnumerator([CcbDevice()], [PendingDevice()]);
        var source = new RecordingChangeSource();
        await using var monitor = CreateCcbMonitor(enumerator, source, maxRetries: 2);
        await monitor.StartAsync();
        Assert.Single(await monitor.GetConnectedDevicesAsync());
        var publications = new List<IReadOnlyList<UsbDeviceDescriptor>>();
        var absent = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += (_, args) =>
        {
            publications.Add(args.Devices);
            absent.TrySetResult(enumerator.CallCount);
            return Task.CompletedTask;
        };

        source.RaiseChanged();
        var callsAtPublication = await absent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(4, callsAtPublication);
        Assert.Empty(Assert.Single(Assert.Single(publications)).CdRomNames);
    }

    [Fact]
    public async Task CcbMetadataLoss_PhysicalRemovalAfterExhaustionReportsAbsenceBeforeRetryingOtherStorage()
    {
        var enumerator = new SequenceEnumerator(
            [CcbDevice()], [PendingDevice()], [PendingDevice()], [PendingDevice("USB\\OTHER")]);
        var source = new RecordingChangeSource();
        await using var monitor = CreateCcbMonitor(enumerator, source, maxRetries: 1,
            retryDelay: TimeSpan.FromMilliseconds(100));
        await monitor.StartAsync();
        Assert.Single(await monitor.GetConnectedDevicesAsync());
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += (_, args) =>
        {
            if (args.Devices.Any(device => device.InstanceId == "USB\\KEY"))
            {
                exhausted.TrySetResult();
            }
            else
            {
                removed.TrySetResult(enumerator.CallCount);
            }
            return Task.CompletedTask;
        };

        source.RaiseChanged();
        await exhausted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, enumerator.CallCount);
        source.RaiseChanged();
        var callsAtPhysicalRemoval = await removed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(4, callsAtPhysicalRemoval);
    }

    [Fact]
    public async Task CcbInitialScan_RetriesUntilCdMetadataArrivesWithoutWaitingForVolume()
    {
        var enumerator = new SequenceEnumerator([PendingDevice()], [CcbDevice(), PendingDevice("OTHER")]);
        await using var monitor = CreateCcbMonitor(enumerator);

        var result = await monitor.GetConnectedDevicesAsync();

        Assert.Equal("CCB USB CD", result[0].CdRomNames.Single());
        Assert.Empty(result[0].VolumeLabels);
        Assert.Equal(2, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbInitialScan_StopsAtConfiguredExtraEnumerationLimit()
    {
        var enumerator = new SequenceEnumerator([PendingDevice()]);
        await using var monitor = CreateCcbMonitor(enumerator, maxRetries: 2);

        var result = await monitor.GetConnectedDevicesAsync();

        Assert.Single(result);
        Assert.Equal(3, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbInitialScan_DefaultLimitAllowsOnlyFiveExtraEnumerations()
    {
        var enumerator = new SequenceEnumerator([PendingDevice()]);
        await using var monitor = CreateCcbMonitor(enumerator);

        Assert.Single(await monitor.GetConnectedDevicesAsync());
        Assert.Equal(6, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbInitialScan_SharedInstanceIdWithMatchingMetadataDoesNotDelayMatch()
    {
        var enumerator = new SequenceEnumerator([PendingDevice(), CcbDevice()]);
        await using var monitor = CreateCcbMonitor(enumerator, retryDelay: TimeSpan.FromMinutes(1));

        var result = await monitor.GetConnectedDevicesAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, result.Count);
        Assert.Equal("CCB USB CD", result[1].CdRomNames.Single());
        Assert.Equal(1, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbInitialScan_StopsWhenCandidateRemoved()
    {
        var enumerator = new SequenceEnumerator([PendingDevice()], []);
        await using var monitor = CreateCcbMonitor(enumerator);

        Assert.Empty(await monitor.GetConnectedDevicesAsync());
        Assert.Equal(2, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbInitialScan_WithoutStorageCandidateDoesNotRetry()
    {
        var enumerator = new SequenceEnumerator([CreateDevice()]);
        await using var monitor = CreateCcbMonitor(enumerator);

        Assert.Single(await monitor.GetConnectedDevicesAsync());
        Assert.Equal(1, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbInitialScan_StorageServiceOnNonUsbNodeDoesNotRetry()
    {
        var enumerator = new SequenceEnumerator([PendingDevice("SCSI\\DEVICE")]);
        await using var monitor = CreateCcbMonitor(enumerator);

        Assert.Single(await monitor.GetConnectedDevicesAsync());
        Assert.Equal(1, enumerator.CallCount);
    }

    [Fact]
    public async Task LegacyInitialScan_DoesNotRetryIncompleteStorageMetadata()
    {
        var enumerator = new SequenceEnumerator([PendingDevice()]);
        await using var monitor = new WindowsUsbDeviceMonitor(enumerator, new RecordingChangeSource(), TimeSpan.Zero);

        Assert.Single(await monitor.GetConnectedDevicesAsync());
        Assert.Equal(1, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbEventScan_PublishesResolvedMetadataOnce()
    {
        var enumerator = new SequenceEnumerator([PendingDevice()], [CcbDevice()]);
        var source = new RecordingChangeSource();
        await using var monitor = CreateCcbMonitor(enumerator, source);
        var publications = new List<IReadOnlyList<UsbDeviceDescriptor>>();
        var matched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += (_, args) =>
        {
            publications.Add(args.Devices);
            if (args.Devices.Any(device => device.CdRomNames.Contains("CCB USB CD")))
            {
                matched.TrySetResult();
            }
            return Task.CompletedTask;
        };
        await monitor.StartAsync();

        source.RaiseChanged();
        await matched.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(publications);
        Assert.Equal(2, enumerator.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CcbEventScan_StopsAtLimitOrRemovalAndPublishesFreshFinalSnapshot(bool removed)
    {
        var enumerator = removed
            ? new SequenceEnumerator([PendingDevice()], [])
            : new SequenceEnumerator([PendingDevice()]);
        var source = new RecordingChangeSource();
        await using var monitor = CreateCcbMonitor(enumerator, source, maxRetries: 2);
        var published = new TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += (_, args) => { published.TrySetResult(args.Devices); return Task.CompletedTask; };
        await monitor.StartAsync();

        source.RaiseChanged();
        var result = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(removed ? 0 : 1, result.Count);
        Assert.Equal(removed ? 2 : 3, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbEventScan_NewRemovalEventStopsObsoleteMetadataRetry()
    {
        var enumerator = new SequenceEnumerator([PendingDevice()], []);
        var source = new RecordingChangeSource();
        await using var monitor = CreateCcbMonitor(enumerator, source, retryDelay: TimeSpan.FromMinutes(1));
        var published = new TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        monitor.DevicesChanged += (_, args) => { Interlocked.Increment(ref count); published.TrySetResult(args.Devices); return Task.CompletedTask; };
        await monitor.StartAsync();
        source.RaiseChanged();
        await enumerator.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        source.RaiseChanged();
        Assert.Empty(await published.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, count);
        Assert.Equal(2, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbEventBurst_DebouncesDuplicateEventsWithSharedInstanceIds()
    {
        var enumerator = new SequenceEnumerator([PendingDevice(), CcbDevice()]);
        var source = new RecordingChangeSource();
        await using var monitor = new WindowsUsbDeviceMonitor(enumerator, source, TimeSpan.FromMilliseconds(20),
            enableCcbMetadataRetry: true, metadataRetryDelay: TimeSpan.FromMinutes(1));
        var published = new TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += (_, args) => { published.TrySetResult(args.Devices); return Task.CompletedTask; };
        await monitor.StartAsync();

        source.RaiseChanged();
        source.RaiseChanged();
        source.RaiseChanged();
        Assert.Equal(2, (await published.Task.WaitAsync(TimeSpan.FromSeconds(2))).Count);
        Assert.Equal(1, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbInitialScan_SynchronousCancellationContinuationSeesReplacementGeneration()
    {
        var enumerator = new ControlledEnumerator();
        var source = new RecordingChangeSource();
        await using var monitor = CreateCcbMonitor(enumerator, source);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var publications = 0;
        monitor.DevicesChanged += (_, _) => { Interlocked.Increment(ref publications); return Task.CompletedTask; };
        await monitor.StartAsync();
        enumerator.SecondResult.TrySetResult([CcbDevice()]);

        // Execute outside xUnit's synchronization context, as native watcher
        // callbacks do. Await the outer task so the read is already suspended.
        var initial = await Task.Factory.StartNew(
            () => monitor.GetConnectedDevicesAsync(deadline.Token),
            CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        try
        {
            await Task.Factory.StartNew(source.RaiseChanged,
                CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default)
                .WaitAsync(TimeSpan.FromSeconds(4));
            var snapshot = await initial.WaitAsync(TimeSpan.FromSeconds(4));

            Assert.Equal("CCB USB CD", Assert.Single(snapshot).CdRomNames.Single());
            Assert.False(deadline.IsCancellationRequested);
            Assert.Equal(0, publications);
        }
        finally
        {
            deadline.Cancel();
            enumerator.FirstResult.TrySetResult([]);
        }
    }

    [Fact]
    public async Task CcbInitialScan_NewEventDiscardsSlowCanceledSnapshotWithoutPublishingIt()
    {
        var enumerator = new ControlledEnumerator();
        var source = new RecordingChangeSource();
        await using var monitor = CreateCcbMonitor(enumerator, source);
        var publications = 0;
        monitor.DevicesChanged += (_, _) => { Interlocked.Increment(ref publications); return Task.CompletedTask; };
        await monitor.StartAsync();
        var initial = monitor.GetConnectedDevicesAsync();
        await enumerator.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        source.RaiseChanged();
        await enumerator.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        enumerator.SecondResult.TrySetResult([]);
        var result = await initial.WaitAsync(TimeSpan.FromSeconds(2));
        enumerator.FirstResult.TrySetResult([CcbDevice()]);
        await enumerator.FirstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(result);
        Assert.Equal(0, publications);
    }

    [Fact]
    public async Task CcbInitialScan_CancellationStopsEvenAnEnumeratorIgnoringCancellation()
    {
        var enumerator = new ControlledEnumerator();
        await using var monitor = CreateCcbMonitor(enumerator);
        using var cancellation = new CancellationTokenSource();
        var initial = monitor.GetConnectedDevicesAsync(cancellation.Token);
        await enumerator.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initial.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            enumerator.FirstResult.TrySetResult([CcbDevice()]);
        }
    }

    [Fact]
    public async Task CcbInitialScan_DisposalCancelsPendingMetadataRetry()
    {
        var enumerator = new SequenceEnumerator([PendingDevice()]);
        var monitor = CreateCcbMonitor(enumerator, retryDelay: TimeSpan.FromMinutes(1));
        var initial = monitor.GetConnectedDevicesAsync();
        await enumerator.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initial.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbInitialScan_SubsequentReadDoesNotReusePreviousMatch()
    {
        var enumerator = new SequenceEnumerator([CcbDevice()], []);
        await using var monitor = CreateCcbMonitor(enumerator);

        Assert.Single(await monitor.GetConnectedDevicesAsync());
        Assert.Empty(await monitor.GetConnectedDevicesAsync());
        Assert.Equal(2, enumerator.CallCount);
    }

    [Fact]
    public async Task CcbEventScan_DisposalStopsPendingMetadataRetry()
    {
        var enumerator = new SequenceEnumerator([PendingDevice()]);
        var source = new RecordingChangeSource();
        var monitor = CreateCcbMonitor(enumerator, source, retryDelay: TimeSpan.FromMinutes(1));
        var publications = 0;
        monitor.DevicesChanged += (_, _) => { Interlocked.Increment(ref publications); return Task.CompletedTask; };
        await monitor.StartAsync();
        source.RaiseChanged();
        await enumerator.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, enumerator.CallCount);
        Assert.Equal(0, publications);
    }

    private static WindowsUsbDeviceMonitor CreateCcbMonitor(
        IWindowsUsbDeviceEnumerator enumerator,
        RecordingChangeSource? source = null,
        int maxRetries = 5,
        TimeSpan? retryDelay = null) => new(
            enumerator, source ?? new RecordingChangeSource(), TimeSpan.Zero,
            enableCcbMetadataRetry: true,
            metadataRetryDelay: retryDelay ?? TimeSpan.FromMilliseconds(10),
            maxMetadataRetries: maxRetries);

    private static UsbDeviceDescriptor PendingDevice(string instanceId = "USB\\KEY") => new(
        instanceId, [], null, [], [], [], Service: "USBSTOR");

    private static UsbDeviceDescriptor CcbDevice() => PendingDevice() with { CdRomNames = ["CCB USB CD"] };

    private sealed class SequenceEnumerator(params IReadOnlyList<UsbDeviceDescriptor>[] snapshots) : IWindowsUsbDeviceEnumerator
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<UsbDeviceDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            FirstStarted.TrySetResult();
            if (call == 3)
            {
                ThirdStarted.TrySetResult();
            }
            return Task.FromResult(snapshots[Math.Min(call - 1, snapshots.Length - 1)]);
        }
    }

    [Fact]
    public async Task GetConnectedDevicesAsync_ReturnsCurrentEnumeratorSnapshot()
    {
        var expected = new[] { CreateDevice() };
        var enumerator = new RecordingEnumerator(expected);
        var changeSource = new RecordingChangeSource();
        await using var monitor = new WindowsUsbDeviceMonitor(
            enumerator,
            changeSource,
            TimeSpan.FromMilliseconds(10));

        var result = await monitor.GetConnectedDevicesAsync();

        Assert.Equal(expected, result);
        Assert.Equal(1, enumerator.CallCount);
    }

    [Fact]
    public async Task StartAsync_StartsDeviceChangeSourceOnce()
    {
        var changeSource = new RecordingChangeSource();
        await using var monitor = new WindowsUsbDeviceMonitor(
            new RecordingEnumerator([]),
            changeSource,
            TimeSpan.FromMilliseconds(10));

        await monitor.StartAsync();
        await monitor.StartAsync();

        Assert.Equal(1, changeSource.StartCount);
    }

    [Fact]
    public async Task ChangeBurst_PublishesOneDebouncedFreshSnapshot()
    {
        var expected = new[] { CreateDevice() };
        var enumerator = new RecordingEnumerator(expected);
        var changeSource = new RecordingChangeSource();
        await using var monitor = new WindowsUsbDeviceMonitor(
            enumerator,
            changeSource,
            TimeSpan.FromMilliseconds(20));
        var published = new TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += (_, args) =>
        {
            published.TrySetResult(args.Devices);
            return Task.CompletedTask;
        };
        await monitor.StartAsync();

        changeSource.RaiseChanged();
        changeSource.RaiseChanged();
        changeSource.RaiseChanged();
        var result = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expected, result);
        Assert.Equal(1, enumerator.CallCount);
    }

    [Fact]
    public async Task DisposeAsync_CancelsPendingRefreshAndStopsChangeSource()
    {
        var enumerator = new RecordingEnumerator([CreateDevice()]);
        var changeSource = new RecordingChangeSource();
        var monitor = new WindowsUsbDeviceMonitor(
            enumerator,
            changeSource,
            TimeSpan.FromMinutes(1));
        await monitor.StartAsync();
        changeSource.RaiseChanged();

        await monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, changeSource.StopCount);
        Assert.Equal(0, enumerator.CallCount);
    }

    [Fact]
    public async Task RefreshFailure_PublishesTheEnumerationError()
    {
        var changeSource = new RecordingChangeSource();
        await using var monitor = new WindowsUsbDeviceMonitor(
            new ThrowingEnumerator(),
            changeSource,
            TimeSpan.FromMilliseconds(10));
        var publishedError = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += (_, args) =>
        {
            if (args.Error is not null)
            {
                publishedError.TrySetResult(args.Error);
            }

            return Task.CompletedTask;
        };
        await monitor.StartAsync();

        changeSource.RaiseChanged();
        var error = await publishedError.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("设备枚举失败", error.Message);
    }

    [Fact]
    public async Task StartFailure_CanBeRetriedWithoutDuplicateSubscriptions()
    {
        var changeSource = new FaultOnceChangeSource();
        await using var monitor = new WindowsUsbDeviceMonitor(
            new RecordingEnumerator([]),
            changeSource,
            TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.StartAsync());
        await monitor.StartAsync();

        Assert.Equal(2, changeSource.StartCount);
        Assert.Equal(1, changeSource.ChangedSubscriberCount);
        Assert.Equal(1, changeSource.FaultedSubscriberCount);
    }

    [Fact]
    public async Task ChangeSourceFault_PublishesDiagnosticError()
    {
        var changeSource = new RecordingChangeSource();
        await using var monitor = new WindowsUsbDeviceMonitor(
            new RecordingEnumerator([]),
            changeSource,
            TimeSpan.Zero);
        var publishedError = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += (_, args) =>
        {
            if (args.Error is not null)
            {
                publishedError.TrySetResult(args.Error);
            }

            return Task.CompletedTask;
        };
        await monitor.StartAsync();

        changeSource.RaiseFault(new InvalidOperationException("监听意外终止"));

        var error = await publishedError.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("监听意外终止", error.Message);

        await monitor.StartAsync();

        Assert.Equal(2, changeSource.StartCount);
        Assert.Equal(1, changeSource.StopCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderCanceledRefresh_CannotPublishAfterNewerSnapshot(bool enableCcbMetadataRetry)
    {
        var enumerator = new ControlledEnumerator();
        var changeSource = new RecordingChangeSource();
        await using var monitor = new WindowsUsbDeviceMonitor(
            enumerator,
            changeSource,
            TimeSpan.Zero, enableCcbMetadataRetry: enableCcbMetadataRetry);
        var publications = new List<IReadOnlyList<UsbDeviceDescriptor>>();
        var firstPublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += (_, args) =>
        {
            publications.Add(args.Devices);
            firstPublication.TrySetResult();
            return Task.CompletedTask;
        };
        await monitor.StartAsync();

        changeSource.RaiseChanged();
        await enumerator.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        changeSource.RaiseChanged();
        await enumerator.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var newest = CreateDevice() with { InstanceId = "NEWEST" };
        enumerator.SecondResult.TrySetResult([newest]);
        await firstPublication.Task.WaitAsync(TimeSpan.FromSeconds(2));
        enumerator.FirstResult.TrySetResult([CreateDevice() with { InstanceId = "STALE" }]);
        await enumerator.FirstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        var published = Assert.Single(publications);
        Assert.Equal("NEWEST", Assert.Single(published).InstanceId);
    }

    [Fact]
    public async Task ChangeSourceFault_InvalidatesRefreshAlreadyInFlight()
    {
        var enumerator = new ControlledEnumerator();
        var changeSource = new RecordingChangeSource();
        await using var monitor = new WindowsUsbDeviceMonitor(
            enumerator,
            changeSource,
            TimeSpan.Zero);
        var publications = new List<UsbDevicesChangedEventArgs>();
        var faultPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.DevicesChanged += (_, args) =>
        {
            publications.Add(args);
            if (args.Error is not null)
            {
                faultPublished.TrySetResult();
            }

            return Task.CompletedTask;
        };
        await monitor.StartAsync();

        changeSource.RaiseChanged();
        await enumerator.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        changeSource.RaiseFault(new InvalidOperationException("监听中止"));
        await faultPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        enumerator.FirstResult.TrySetResult([CreateDevice()]);
        await enumerator.FirstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        var publication = Assert.Single(publications);
        Assert.Equal("监听中止", publication.Error?.Message);
    }

    private static UsbDeviceDescriptor CreateDevice() => new(
        "USB\\VID_1677&PID_0054\\INSTANCE",
        ["USB\\VID_1677&PID_0054"],
        "uKeyCI800-K156",
        [],
        [],
        []);

    private sealed class RecordingEnumerator(
        IReadOnlyList<UsbDeviceDescriptor> devices) : IWindowsUsbDeviceEnumerator
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<UsbDeviceDescriptor>> EnumerateAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(devices);
        }
    }

    private sealed class RecordingChangeSource : IDeviceChangeSource
    {
        public event EventHandler? Changed;

        public event EventHandler<DeviceChangeSourceFaultedEventArgs>? Faulted;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public void Start() => StartCount++;

        public void Stop() => StopCount++;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public void RaiseFault(Exception exception) =>
            Faulted?.Invoke(this, new(exception));

        public void Dispose()
        {
        }
    }

    private sealed class FaultOnceChangeSource : IDeviceChangeSource
    {
        private EventHandler? _changed;
        private EventHandler<DeviceChangeSourceFaultedEventArgs>? _faulted;

        public event EventHandler? Changed
        {
            add
            {
                _changed += value;
                ChangedSubscriberCount++;
            }
            remove
            {
                _changed -= value;
                ChangedSubscriberCount--;
            }
        }

        public event EventHandler<DeviceChangeSourceFaultedEventArgs>? Faulted
        {
            add
            {
                _faulted += value;
                FaultedSubscriberCount++;
            }
            remove
            {
                _faulted -= value;
                FaultedSubscriberCount--;
            }
        }

        public int StartCount { get; private set; }

        public int ChangedSubscriberCount { get; private set; }

        public int FaultedSubscriberCount { get; private set; }

        public void Start()
        {
            StartCount++;
            if (StartCount == 1)
            {
                throw new InvalidOperationException("启动监听失败");
            }
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ControlledEnumerator : IWindowsUsbDeviceEnumerator
    {
        private int _callCount;

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>> FirstResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<IReadOnlyList<UsbDeviceDescriptor>> SecondResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<UsbDeviceDescriptor>> EnumerateAsync(
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                FirstStarted.TrySetResult();
                var result = await FirstResult.Task;
                FirstCompleted.TrySetResult();
                return result;
            }

            SecondStarted.TrySetResult();
            return await SecondResult.Task;
        }
    }

    private sealed class ThrowingEnumerator : IWindowsUsbDeviceEnumerator
    {
        public Task<IReadOnlyList<UsbDeviceDescriptor>> EnumerateAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<UsbDeviceDescriptor>>(
                new InvalidOperationException("设备枚举失败"));
    }
}
