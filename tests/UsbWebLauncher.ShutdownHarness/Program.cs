using UsbWebLauncher.App;

// A separate process is essential: the deadline must work when its main thread
// cannot pump messages, run async continuations, or complete a cancellation callback.
var mode = args[0];
if (mode.StartsWith("stop-", StringComparison.Ordinal))
{
    using var stopDeadline = new BusinessSessionStopDeadline(
        TimeSpan.FromMilliseconds(700), TimeSpan.FromMilliseconds(150));
    Console.WriteLine("armed");
    Console.Out.Flush();
    if (mode == "stop-responsive-timeout")
    {
        var token = stopDeadline.Token;
        try { await new TaskCompletionSource().Task.WaitAsync(token); }
        catch (OperationCanceledException) { stopDeadline.Dispose(); }
        await Task.Delay(1000);
        return 23;
    }
    if (mode == "stop-close-independent")
    {
        using var closeDeadline = new ApplicationExitWatchdog(TimeSpan.FromMilliseconds(450));
        closeDeadline.Arm();
        stopDeadline.Dispose();
        Thread.Sleep(Timeout.Infinite);
    }
    Thread.Sleep(Timeout.Infinite);
}
var timeout = TimeSpan.FromMilliseconds(350);
using var watchdog = new ApplicationExitWatchdog(timeout);
watchdog.Arm();
Console.WriteLine("armed");
Console.Out.Flush();

if (mode == "disarm")
{
    watchdog.Dispose();
    Thread.Sleep(900);
    return 23;
}

if (mode == "repeat")
{
    while (true)
    {
        Thread.Sleep(40);
        watchdog.Arm();
    }
}

if (mode == "blocked-cancel")
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Token.Register(() => Thread.Sleep(Timeout.Infinite));
    cancellation.Cancel();
}
else if (mode == "blocked-thread-pool")
{
    ThreadPool.SetMinThreads(1, 1);
    ThreadPool.SetMaxThreads(1, 1);
    ThreadPool.QueueUserWorkItem(_ => Thread.Sleep(Timeout.Infinite));
    Thread.Sleep(Timeout.Infinite);
}
else if (mode == "never-completing-cleanup")
{
    var sequence = new ApplicationShutdownSequence(
        () => new TaskCompletionSource().Task,
        () => { },
        Environment.Exit);
    await sequence.RunAsync();
}
else
{
    Thread.Sleep(Timeout.Infinite);
}

return 29;
