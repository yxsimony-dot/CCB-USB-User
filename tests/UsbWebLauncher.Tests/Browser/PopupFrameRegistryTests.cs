using UsbWebLauncher.App.Browser;

namespace UsbWebLauncher.Tests.Browser;

public sealed class PopupFrameRegistryTests
{
    [Fact]
    public void DestroyedNotificationRetiresOriginalFrameWithoutUsingEventSenderOrNativeUnsubscribe()
    {
        var registry = new PopupFrameRegistry<object>();
        var originalFrame = new object();
        Action destroyed = null!;
        var calls = 0;
        Assert.True(registry.Register(originalFrame, callback => destroyed = callback));
        registry.Dispatch(originalFrame, () => calls++);
        destroyed(); // Closure belongs to originalFrame; native sender identity is irrelevant.
        registry.Dispatch(originalFrame, () => calls++);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DisposalBlocksQueuedCallbacksAndLateFrameCreationWithoutTouchingNativeFrames()
    {
        var registry = new PopupFrameRegistry<object>();
        var frame = new object();
        Action destroyed = null!;
        Assert.True(registry.Register(frame, callback => destroyed = callback));
        registry.Dispose();
        registry.Dispose();
        var calls = 0;
        registry.Dispatch(frame, () => calls++);
        destroyed();
        Assert.False(registry.Register(new object(), _ => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DuplicateFrameDoesNotAccumulateNativeSubscriptions()
    {
        var registry = new PopupFrameRegistry<object>();
        var frame = new object();
        var subscriptions = 0;
        Assert.True(registry.Register(frame, _ => subscriptions++));
        Assert.False(registry.Register(frame, _ => subscriptions++));
        Assert.Equal(1, subscriptions);
    }
}
