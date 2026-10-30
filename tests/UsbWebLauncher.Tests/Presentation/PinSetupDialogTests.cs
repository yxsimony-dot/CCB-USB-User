using System.Security.Cryptography;
using System.Windows.Controls;
using UsbWebLauncher.App;
using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class PinSetupDialogTests
{
    [Fact]
    public async Task SuccessfulConfirmationUsesMaskedInputAndClearsTheControl()
    {
        var secret = RandomSecret();
        try
        {
            var result = await RunStaAsync(async () =>
            {
                PinSetupDialog? dialog = null;
                PasswordBox? passwordBox = null;
                dialog = new PinSetupDialog(null, () =>
                {
                    passwordBox = Assert.IsType<PasswordBox>(
                        dialog!.FindName("PinPasswordBox"));
                    passwordBox.Password = new string(secret);
                    return true;
                });

                var pin = await dialog.ShowAsync();
                using var clearedPassword = passwordBox!.SecurePassword;
                return (
                    Pin: pin,
                    PasswordLength: clearedPassword.Length,
                    PasswordChar: passwordBox.PasswordChar);
            });

            using var pin = Assert.IsType<ClientCertificatePin>(result.Pin);
            Assert.True(pin.AsMemory().Span.SequenceEqual(secret));
            Assert.Equal(0, result.PasswordLength);
            Assert.Equal('*', result.PasswordChar);
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
            PasswordBox? passwordBox = null;
            dialog = new PinSetupDialog(null, () =>
            {
                passwordBox = Assert.IsType<PasswordBox>(
                    dialog!.FindName("PinPasswordBox"));
                passwordBox.Password = Guid.NewGuid().ToString("N");
                return null;
            });

            var pin = await dialog.ShowAsync();
            using var clearedPassword = passwordBox!.SecurePassword;
            return (Pin: pin, PasswordLength: clearedPassword.Length);
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
            PasswordBox? passwordBox = null;
            dialog = new PinSetupDialog(null, () =>
            {
                passwordBox = Assert.IsType<PasswordBox>(
                    dialog!.FindName("PinPasswordBox"));
                passwordBox.Password = Guid.NewGuid().ToString("N");
                source.Cancel();
                return true;
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => dialog.ShowAsync(source.Token));
            using var clearedPassword = passwordBox!.SecurePassword;
            return clearedPassword.Length;
        });

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task DialogFailureClearsTheControl()
    {
        var result = await RunStaAsync(async () =>
        {
            PinSetupDialog? dialog = null;
            PasswordBox? passwordBox = null;
            dialog = new PinSetupDialog(null, () =>
            {
                passwordBox = Assert.IsType<PasswordBox>(
                    dialog!.FindName("PinPasswordBox"));
                passwordBox.Password = Guid.NewGuid().ToString("N");
                throw new InvalidOperationException("synthetic dialog failure");
            });

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dialog.ShowAsync());
            using var clearedPassword = passwordBox!.SecurePassword;
            return clearedPassword.Length;
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
