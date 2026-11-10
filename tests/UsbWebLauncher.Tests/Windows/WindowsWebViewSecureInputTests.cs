using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Windows.Authentication;
using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows;

public sealed class WindowsWebViewSecureInputTests
{
    private static readonly IntPtr Owner = new(10), Target = new(20);

    [Fact]
    public void PrepareRequiresStableFocusAndReadyInputMethod()
    {
        var native = new Native();
        var input = new WindowsWebViewSecureInput(native);
        Assert.Equal(Target, input.Prepare(Owner, 100));
        native.Focus = IntPtr.Zero;
        Assert.Equal(IntPtr.Zero, input.Prepare(Owner, 100));
        native.Focus = Target;
        native.ModifiersReleased = false;
        Assert.Equal(IntPtr.Zero, input.Prepare(Owner, 100));
        native.ModifiersReleased = true;
        native.ImeReady = false;
        Assert.Equal(IntPtr.Zero, input.Prepare(Owner, 100));
        native.ImeReady = true;
        native.OnPrepare = () => native.Focus = IntPtr.Zero;
        Assert.Equal(IntPtr.Zero, input.Prepare(Owner, 100));
    }

    [Fact]
    public void SendsOneBatchAndClearsTemporaryBuffers()
    {
        var native = new Native();
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        Assert.True(new WindowsWebViewSecureInput(native).Send(Owner, 100, Target, pin, default));
        Assert.Single(native.Sends);
        Assert.Equal(2, native.Sends[0].Length);
        Assert.All(native.Built!, item => Assert.Equal((ushort)0, item.Data.Keyboard.VirtualKey));
        Assert.NotNull(native.Secret);
        Assert.Throws<ObjectDisposedException>(() => { _ = native.Secret!.Pointer; });
        Assert.Equal("[REDACTED]", pin.ToString());
    }

    [Theory]
    [InlineData("focus")]
    [InlineData("modifiers")]
    [InlineData("ime")]
    public void UnsafeStateDoesNotSend(string failure)
    {
        var native = new Native();
        if (failure == "focus") native.Focus = IntPtr.Zero;
        if (failure == "modifiers") native.ModifiersReleased = false;
        if (failure == "ime") native.ImeReady = false;
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        Assert.False(new WindowsWebViewSecureInput(native).Send(Owner, 100, Target, pin, default));
        Assert.Empty(native.Sends);
        Assert.Null(native.Built);
    }

    [Theory]
    [InlineData("focus")]
    [InlineData("modifiers")]
    [InlineData("ime")]
    public void StateIsRecheckedAfterBuildingBatch(string failure)
    {
        var native = new Native();
        native.OnBuild = () =>
        {
            if (failure == "focus") native.Focus = IntPtr.Zero;
            if (failure == "modifiers") native.ModifiersReleased = false;
            if (failure == "ime") native.ImeReady = false;
        };
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        Assert.False(new WindowsWebViewSecureInput(native).Send(Owner, 100, Target, pin, default));
        Assert.Empty(native.Sends);
        Assert.All(native.Built!, item => Assert.Equal((ushort)0, item.Data.Keyboard.VirtualKey));
    }

    [Fact]
    public void CancellationDuringBuildPreventsSendingAndClearsBatch()
    {
        using var stop = new CancellationTokenSource();
        var native = new Native { OnBuild = stop.Cancel };
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new WindowsWebViewSecureInput(native).Send(Owner, 100, Target, pin, stop.Token));
        Assert.Empty(native.Sends);
        Assert.All(native.Built!, item => Assert.Equal((ushort)0, item.Data.Keyboard.VirtualKey));
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(1u, 2)]
    public void PartialBatchOnlyReleasesAcceptedDownKeysAndNeverRetries(uint accepted, int sends)
    {
        var native = new Native { Accepted = accepted };
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        Assert.False(new WindowsWebViewSecureInput(native).Send(Owner, 100, Target, pin, default));
        Assert.Equal(sends, native.Sends.Count);
        if (accepted == 1)
        {
            var release = Assert.Single(native.Sends[1]);
            Assert.Equal((ushort)0x41, release.Data.Keyboard.VirtualKey);
            Assert.Equal(NativePromptInput.KeyUpFlag, release.Data.Keyboard.Flags);
        }
        Assert.All(native.Built!, item => Assert.Equal((ushort)0, item.Data.Keyboard.VirtualKey));
    }

    [Fact]
    public void SendFailureStillClearsBatch()
    {
        var native = new Native { ThrowOnSend = true };
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        Assert.Throws<InvalidOperationException>(() =>
            new WindowsWebViewSecureInput(native).Send(Owner, 100, Target, pin, default));
        Assert.All(native.Built!, item => Assert.Equal((ushort)0, item.Data.Keyboard.VirtualKey));
    }

    private sealed class Native : IWebViewSecureInputNative
    {
        public IntPtr Focus = Target;
        public bool ModifiersReleased = true, ImeReady = true, ThrowOnSend;
        public uint? Accepted;
        public Action? OnBuild, OnPrepare;
        public ClearableSecretBuffer? Secret;
        public NativePromptInput[]? Built;
        public List<NativePromptInput[]> Sends { get; } = [];
        public IntPtr CaptureFocus(IntPtr owner, uint browserProcessId) => Focus;
        public bool PrepareInputMethod(IntPtr target) { OnPrepare?.Invoke(); return ImeReady; }
        public bool IsInputMethodReady(IntPtr target) => ImeReady;
        public bool AreModifiersReleased() => ModifiersReleased;
        public NativePromptInput[] Build(ClearableSecretBuffer pin, IntPtr target)
        {
            Secret = pin;
            Built = [NativePromptInput.VirtualKey(0x41, false), NativePromptInput.VirtualKey(0x41, true)];
            OnBuild?.Invoke();
            return Built;
        }
        public uint Send(NativePromptInput[] inputs)
        {
            Sends.Add(inputs.ToArray());
            if (ThrowOnSend) throw new InvalidOperationException();
            return Accepted ?? (uint)inputs.Length;
        }
    }
}
