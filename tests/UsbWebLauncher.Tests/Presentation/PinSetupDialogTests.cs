using System.Security.Cryptography;
using System.Windows.Controls;
using UsbWebLauncher.App;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class PinSetupDialogTests
{
    [Theory]
    [InlineData("建设银行")]
    [InlineData("邮储银行")]
    public async Task DialogLabelsTheSelectedBank(string bankDisplayName)
    {
        var result = await RunStaAsync(() =>
        {
            PinSetupDialog? dialog = null;
            dialog = new PinSetupDialog(null, bankDisplayName, () =>
            {
                var heading = Assert.IsType<TextBlock>(
                    dialog!.FindName("HeadingTextBlock"));
                var description = Assert.IsType<TextBlock>(
                    dialog.FindName("DescriptionTextBlock"));
                return heading.Text.Contains(bankDisplayName) &&
                    description.Text.Contains(bankDisplayName)
                    ? false
                    : throw new Xunit.Sdk.XunitException(
                        "The PIN dialog was not bound to the selected bank.");
            });

            return dialog.ShowAsync();
        });

        Assert.Null(result);
    }

    [Fact]
    public async Task SuccessfulConfirmationUsesVisibleInputWithoutUndoAndClearsTheControl()
    {
        var secret = RandomSecret();
        try
        {
            var result = await RunStaAsync(async () =>
            {
                PinSetupDialog? dialog = null;
                TextBox? textBox = null;
                dialog = new PinSetupDialog(null, () =>
                {
                    textBox = Assert.IsType<TextBox>(
                        dialog!.FindName("PinTextBox"));
                    textBox.Text = new string(secret);
                    Assert.True(textBox.Text.AsSpan().SequenceEqual(secret));
                    Assert.False(textBox.IsUndoEnabled);
                    return true;
                });

                var pin = await dialog.ShowAsync();
                return (
                    Pin: pin,
                    PasswordLength: textBox!.Text.Length,
                    CanUndo: textBox.CanUndo);
            });

            using var pin = Assert.IsType<ClientCertificatePin>(result.Pin);
            Assert.True(pin.AsMemory().Span.SequenceEqual(secret));
            Assert.Equal(0, result.PasswordLength);
            Assert.False(result.CanUndo);
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
        }
    }

    [Fact]
    public async Task UnexpectedClosureReturnsNoPinAndClearsTheControl()
    {
        var result = await RunStaAsync(async () =>
        {
            PinSetupDialog? dialog = null;
            TextBox? textBox = null;
            dialog = new PinSetupDialog(null, () =>
            {
                textBox = Assert.IsType<TextBox>(
                    dialog!.FindName("PinTextBox"));
                textBox.Text = Guid.NewGuid().ToString("N");
                return null;
            });

            var pin = await dialog.ShowAsync();
            return (Pin: pin, PasswordLength: textBox!.Text.Length);
        });

        Assert.Null(result.Pin);
        Assert.Equal(0, result.PasswordLength);
    }

    [Fact]
    public async Task CancellationClearsTheControl()
    {
        var result = await RunStaAsync(async () =>
        {
            using var source = new CancellationTokenSource();
            PinSetupDialog? dialog = null;
            TextBox? textBox = null;
            dialog = new PinSetupDialog(null, () =>
            {
                textBox = Assert.IsType<TextBox>(
                    dialog!.FindName("PinTextBox"));
                textBox.Text = Guid.NewGuid().ToString("N");
                source.Cancel();
                return true;
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => dialog.ShowAsync(source.Token));
            return textBox!.Text.Length;
        });

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task DialogFailureClearsTheControl()
    {
        var result = await RunStaAsync(async () =>
        {
            PinSetupDialog? dialog = null;
            TextBox? textBox = null;
            dialog = new PinSetupDialog(null, () =>
            {
                textBox = Assert.IsType<TextBox>(
                    dialog!.FindName("PinTextBox"));
                textBox.Text = Guid.NewGuid().ToString("N");
                throw new InvalidOperationException("synthetic dialog failure");
            });

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dialog.ShowAsync());
            return textBox!.Text.Length;
        });

        Assert.Equal(0, result);
    }

    private static Task<T> RunStaAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(async () =>
        {
            try
            {
                completion.TrySetResult(await action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static char[] RandomSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        var characters = new char[bytes.Length];
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index] = (char)('!' + bytes[index] % 90);
        }

        Array.Clear(bytes, 0, bytes.Length);
        return characters;
    }
}
