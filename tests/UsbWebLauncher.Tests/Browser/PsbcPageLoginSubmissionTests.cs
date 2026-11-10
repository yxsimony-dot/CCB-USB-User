using UsbWebLauncher.App.Browser;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Tests.Browser;

public sealed class PsbcPageLoginSubmissionTests
{
    [Theory]
    [InlineData("https://corpebank.psbc.com/#/login", true)]
    [InlineData("https://corpebank.psbc.com/#/login?tab=ukeyLogin", true)]
    [InlineData("https://corpebank.psbc.com/#/login?tab=userLogin", false)]
    [InlineData("https://corpebank.psbc.com/#/index", false)]
    [InlineData("https://corpebank.psbc.com/path#/login", false)]
    [InlineData("https://corpebank.psbc.com/?x=y#/login", false)]
    [InlineData("http://corpebank.psbc.com/#/login", false)]
    [InlineData("https://corpebank.psbc.com:444/#/login", false)]
    [InlineData("https://user@corpebank.psbc.com/#/login", false)]
    [InlineData("https://corpebankq.psbc.com/#/login", false)]
    [InlineData("https://corpebankcdn.psbc.com/dist/index.html#/login", false)]
    public void AllowsOnlyProfessionalTopLevelLogin(string address, bool allowed) =>
        Assert.Equal(allowed, WpfPsbcCertificateLoginPage.IsAllowedPage(new Uri(address)));

    [Fact]
    public async Task SendsOnceThenWaitsForAcknowledgementBeforeSubmitting()
    {
        var input = new Input("empty", "pending", "filled");
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        Assert.Equal(CertificateLoginResult.Submitted, await Run(input, pin));
        Assert.Equal(1, input.Sends);
        Assert.Equal(1, input.Submits);
        Assert.Equal(1, input.Releases);
    }

    [Theory]
    [InlineData("failed", 0)]
    [InlineData("filled", 0)]
    [InlineData("pending", 0)]
    public async Task DoesNotOverwriteOrSendWhenInitialStateIsNotEmpty(string initial, int sends)
    {
        var input = new Input(initial);
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        Assert.Equal(CertificateLoginResult.Failed, await Run(input, pin));
        Assert.Equal(sends, input.Sends);
        Assert.Equal(0, input.Submits);
        Assert.Equal(1, input.Releases);
    }

    [Fact]
    public async Task PartialSendIsNeverRetriedOrSubmitted()
    {
        var input = new Input("empty") { SendResult = false };
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        Assert.Equal(CertificateLoginResult.Failed, await Run(input, pin));
        Assert.Equal(1, input.Sends);
        Assert.Equal(0, input.Submits);
    }

    [Fact]
    public async Task LostFocusAfterSendStopsBeforeClick()
    {
        var input = new Input("empty", "failed");
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        Assert.Equal(CertificateLoginResult.Failed, await Run(input, pin));
        Assert.Equal(1, input.Sends);
        Assert.Equal(0, input.Submits);
    }

    [Fact]
    public async Task MissingAcknowledgementTimesOutWithoutRetry()
    {
        var input = new Input("empty");
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        Assert.Equal(CertificateLoginResult.Failed, await Run(input, pin));
        Assert.Equal(1, input.Sends);
        Assert.Equal(0, input.Submits);
        Assert.Equal(1, input.Releases);
    }

    [Fact]
    public async Task CancellationAfterSendReleasesControlWithoutClick()
    {
        using var stop = new CancellationTokenSource();
        var input = new Input("empty", "filled") { OnSend = stop.Cancel };
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(input, pin, stop.Token));
        Assert.Equal(1, input.Sends);
        Assert.Equal(0, input.Submits);
        Assert.Equal(1, input.Releases);
    }

    [Fact]
    public async Task AmbiguousClickIsNotRepeated()
    {
        var input = new Input("empty", "filled") { SubmitThrows = true };
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(input, pin));
        Assert.Equal(1, input.Submits);
        Assert.Equal(1, input.Releases);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(21)]
    public async Task InvalidLengthNeverStartsCapture(int length)
    {
        var input = new Input("empty", "filled");
        using var pin = ClientCertificatePin.CopyOf(new string('a', length).AsSpan());
        Assert.Equal(CertificateLoginResult.Unsupported, await Run(input, pin));
        Assert.Equal(0, input.Prepares);
        Assert.Equal(0, input.Sends);
        Assert.Equal(0, input.Submits);
    }

    [Fact]
    public async Task FailedPreparationReleasesWithoutSending()
    {
        var input = new Input("empty") { PrepareResult = false };
        using var pin = ClientCertificatePin.CopyOf("Test123!".AsSpan());
        Assert.Equal(CertificateLoginResult.Failed, await Run(input, pin));
        Assert.Equal(0, input.Sends);
        Assert.Equal(0, input.Submits);
        Assert.Equal(1, input.Releases);
    }

    private static Task<CertificateLoginResult> Run(Input input, ClientCertificatePin pin, CancellationToken token = default) =>
        PsbcPageLoginSubmission.RunAsync(input, pin, token, TimeSpan.FromMilliseconds(250), TimeSpan.Zero);

    private sealed class Input(params string[] statuses) : IPsbcPageLoginInput
    {
        private int _index;
        public int Prepares, Sends, Submits, Releases;
        public bool PrepareResult = true, SendResult = true, SubmitThrows;
        public Action? OnSend;
        public Task<bool> PrepareAsync(CancellationToken token) { Prepares++; return Task.FromResult(PrepareResult); }
        public Task<string?> CheckAsync(int length, CancellationToken token) =>
            Task.FromResult<string?>(statuses[Math.Min(_index++, statuses.Length - 1)]);
        public bool Send(ClientCertificatePin pin, CancellationToken token)
        {
            Sends++;
            OnSend?.Invoke();
            return SendResult;
        }
        public Task<string?> SubmitAsync(int length, CancellationToken token)
        {
            Submits++;
            if (SubmitThrows) throw new InvalidOperationException();
            return Task.FromResult<string?>("submitted");
        }
        public Task ReleaseAsync() { Releases++; return Task.CompletedTask; }
    }
}
