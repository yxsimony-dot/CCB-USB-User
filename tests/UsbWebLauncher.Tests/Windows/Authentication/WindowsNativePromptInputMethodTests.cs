using UsbWebLauncher.Windows.Authentication.NativePrompt;

namespace UsbWebLauncher.Tests.Windows.Authentication;

public sealed class WindowsNativePromptInputMethodTests
{
    [Fact]
    public void Prepare_AlreadyClosed_DoesNotChangeInputMethod()
    {
        var native = new RecordingImeApi();
        native.Reads.Enqueue((true, false));

        Assert.True(new WindowsNativePromptInputMethod(native).Prepare(new IntPtr(41)));
        Assert.Equal(new IntPtr(41), native.Target);
        Assert.Equal(1, native.ReadCount);
        Assert.Equal(0, native.SetCount);
    }

    [Fact]
    public void Prepare_Open_ClosesOnceAndVerifiesClosed()
    {
        var native = new RecordingImeApi();
        native.Reads.Enqueue((true, true));
        native.Reads.Enqueue((true, false));

        Assert.True(new WindowsNativePromptInputMethod(native).Prepare(new IntPtr(41)));
        Assert.Equal(2, native.ReadCount);
        Assert.Equal(1, native.SetCount);
        Assert.All(native.ImeHandles, handle => Assert.Equal(native.Ime, handle));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Prepare_CloseCannotBeConfirmed_RejectsWithoutRetry(bool readSucceeded, bool open)
    {
        var native = new RecordingImeApi();
        native.Reads.Enqueue((true, true));
        native.Reads.Enqueue((readSucceeded, open));

        Assert.False(new WindowsNativePromptInputMethod(native).Prepare(new IntPtr(41)));
        Assert.Equal(2, native.ReadCount);
        Assert.Equal(1, native.SetCount);
    }

    [Fact]
    public void PrepareThenRecheck_DoesNotRepeatSuccessfulClose()
    {
        var native = new RecordingImeApi();
        native.Reads.Enqueue((true, true));
        native.Reads.Enqueue((true, false));
        native.Reads.Enqueue((true, false));
        native.Reads.Enqueue((true, false));
        var inputMethod = new WindowsNativePromptInputMethod(native);

        Assert.True(inputMethod.Prepare(new IntPtr(41)));
        Assert.True(inputMethod.IsReady(new IntPtr(41)));
        Assert.True(inputMethod.Prepare(new IntPtr(41)));
        Assert.Equal(4, native.ReadCount);
        Assert.Equal(1, native.SetCount);
    }

    [Fact]
    public void Prepare_SetTimedOut_RejectsWithoutRetry()
    {
        var native = new RecordingImeApi { SetSucceeded = false };
        native.Reads.Enqueue((true, true));

        Assert.False(new WindowsNativePromptInputMethod(native).Prepare(new IntPtr(41)));
        Assert.Equal(1, native.ReadCount);
        Assert.Equal(1, native.SetCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownInputMethodState_FailsClosedWithoutChangingIt(bool prepare)
    {
        var native = new RecordingImeApi();
        native.Reads.Enqueue((false, false));
        var inputMethod = new WindowsNativePromptInputMethod(native);

        Assert.False(prepare ? inputMethod.Prepare(new IntPtr(41)) : inputMethod.IsReady(new IntPtr(41)));
        Assert.Equal(1, native.ReadCount);
        Assert.Equal(0, native.SetCount);
    }

    [Theory]
    [InlineData(0, 43)]
    [InlineData(41, 0)]
    public void MissingTargetOrIme_FailsClosedWithoutReadingOrChangingIt(int target, int ime)
    {
        var native = new RecordingImeApi { Ime = new IntPtr(ime) };
        var inputMethod = new WindowsNativePromptInputMethod(native);

        Assert.False(inputMethod.Prepare(new IntPtr(target)));
        Assert.False(inputMethod.IsReady(new IntPtr(target)));
        Assert.Equal(0, native.ReadCount);
        Assert.Equal(0, native.SetCount);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IsReady_OnlyChecksStateAndNeverChangesIt(bool open, bool expected)
    {
        var native = new RecordingImeApi();
        native.Reads.Enqueue((true, open));

        Assert.Equal(expected, new WindowsNativePromptInputMethod(native).IsReady(new IntPtr(41)));
        Assert.Equal(1, native.ReadCount);
        Assert.Equal(0, native.SetCount);
    }

    private sealed class RecordingImeApi : IWindowsNativePromptImeApi
    {
        public IntPtr Ime { get; init; } = new(43);
        public bool SetSucceeded { get; init; } = true;
        public IntPtr Target { get; private set; }
        public int ReadCount { get; private set; }
        public int SetCount { get; private set; }
        public Queue<(bool Succeeded, bool Open)> Reads { get; } = new();
        public List<IntPtr> ImeHandles { get; } = new();

        public IntPtr GetDefaultWindow(IntPtr target)
        {
            Target = target;
            return Ime;
        }

        public bool TryGetOpen(IntPtr ime, out bool open)
        {
            ImeHandles.Add(ime);
            ReadCount++;
            var result = Reads.Dequeue();
            open = result.Open;
            return result.Succeeded;
        }

        public bool TrySetClosed(IntPtr ime)
        {
            ImeHandles.Add(ime);
            SetCount++;
            return SetSucceeded;
        }
    }
}
