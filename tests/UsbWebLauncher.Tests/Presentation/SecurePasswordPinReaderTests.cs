using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using UsbWebLauncher.App.Presentation;

namespace UsbWebLauncher.Tests.Presentation;

public sealed class SecurePasswordPinReaderTests
{
    [Fact]
    public void CopyFromEmptySecureStringDoesNotAllocateSecretBuffers()
    {
        using var securePassword = new SecureString();

        using var pin = SecurePasswordPinReader.CopyFrom(
            securePassword,
            _ => throw new InvalidOperationException("allocation is not expected"),
            (_, _) => throw new InvalidOperationException("copy is not expected"),
            _ => throw new InvalidOperationException("free is not expected"));

        Assert.True(pin.AsMemory().IsEmpty);
    }

    [Fact]
    public void CopyFromClearsManagedAndUnmanagedTemporaryBuffers()
    {
        var secret = RandomSecret();
        using var securePassword = ToSecureString(secret);
        char[]? temporaryCharacters = null;
        char[]? nativeAfterClear = null;
        var clearedAndFreed = false;

        try
        {
            using var pin = SecurePasswordPinReader.CopyFrom(
                securePassword,
                _ => Allocate(secret),
                (pointer, characters) =>
                {
                    temporaryCharacters = characters;
                    Marshal.Copy(pointer, characters, 0, characters.Length);
                },
                pointer =>
                {
                    var zeros = new char[secret.Length + 1];
                    Marshal.Copy(zeros, 0, pointer, zeros.Length);
                    nativeAfterClear = new char[zeros.Length];
                    Marshal.Copy(pointer, nativeAfterClear, 0, nativeAfterClear.Length);
                    Marshal.FreeHGlobal(pointer);
                    clearedAndFreed = true;
                });

            Assert.True(pin.AsMemory().Span.SequenceEqual(secret));
            Assert.NotNull(temporaryCharacters);
            Assert.All(temporaryCharacters!, character => Assert.Equal('\0', character));
            Assert.NotNull(nativeAfterClear);
            Assert.All(nativeAfterClear!, character => Assert.Equal('\0', character));
            Assert.True(clearedAndFreed);
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
        }
    }

    [Fact]
    public void CopyFailureStillClearsEveryTemporaryBuffer()
    {
        var secret = RandomSecret();
        using var securePassword = ToSecureString(secret);
        char[]? temporaryCharacters = null;
        var clearedAndFreed = false;

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                SecurePasswordPinReader.CopyFrom(
                    securePassword,
                    _ => Allocate(secret),
                    (pointer, characters) =>
                    {
                        temporaryCharacters = characters;
                        Marshal.Copy(pointer, characters, 0, characters.Length);
                        throw new InvalidDataException("synthetic copy failure");
                    },
                    pointer =>
                    {
                        var zeros = new char[secret.Length + 1];
                        Marshal.Copy(zeros, 0, pointer, zeros.Length);
                        Marshal.FreeHGlobal(pointer);
                        clearedAndFreed = true;
                    }));

            Assert.NotNull(temporaryCharacters);
            Assert.All(temporaryCharacters!, character => Assert.Equal('\0', character));
            Assert.True(clearedAndFreed);
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
        }
    }

    private static IntPtr Allocate(char[] characters)
    {
        var pointer = Marshal.AllocHGlobal((characters.Length + 1) * sizeof(char));
        Marshal.Copy(characters, 0, pointer, characters.Length);
        Marshal.WriteInt16(pointer, characters.Length * sizeof(char), 0);
        return pointer;
    }

    private static SecureString ToSecureString(char[] characters)
    {
        var result = new SecureString();
        foreach (var character in characters)
        {
            result.AppendChar(character);
        }

        result.MakeReadOnly();
        return result;
    }

    private static char[] RandomSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var characters = new char[bytes.Length];
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index] = (char)('!' + bytes[index] % 90);
        }

        Array.Clear(bytes, 0, bytes.Length);
        return characters;
    }
}
