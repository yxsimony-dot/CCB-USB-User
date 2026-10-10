using System.Reflection;
using UsbWebLauncher.Core.Authentication;
using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Tests.Authentication;

public sealed class AuthenticationContractsTests
{
    [Fact]
    public void Contracts_DefinePinStoreAndValidationStampStore()
    {
        var pinStoreType = GetRequiredType("UsbWebLauncher.Core.Authentication.IClientCertificatePinStore");
        var stampStoreType = GetRequiredType("UsbWebLauncher.Core.Authentication.IClientCertificateValidationStampStore");
        var stampType = GetRequiredType("UsbWebLauncher.Core.Authentication.ClientCertificateValidationStamp");

        Assert.True(pinStoreType.IsInterface);
        Assert.True(stampStoreType.IsInterface);
        Assert.Equal("String", stampType.GetProperty("AuthenticationId")?.PropertyType.Name);
        Assert.Equal("String", stampType.GetProperty("AuthenticationHash")?.PropertyType.Name);

        Assert.NotNull(pinStoreType.GetMethod("ReadAsync"));
        Assert.NotNull(pinStoreType.GetMethod("SaveAsync"));
        Assert.NotNull(pinStoreType.GetMethod("DeleteAsync"));
        Assert.NotNull(stampStoreType.GetMethod("ReadAsync"));
        Assert.NotNull(stampStoreType.GetMethod("SaveAsync"));
        Assert.NotNull(stampStoreType.GetMethod("DeleteAsync"));
    }

    [Fact]
    public void Contracts_DefineUnlockerResultAndReleasableSession()
    {
        var unlockerType = GetRequiredType("UsbWebLauncher.Core.Authentication.IClientCertificateTokenUnlocker");
        var sessionType = GetRequiredType("UsbWebLauncher.Core.Authentication.IClientCertificateTokenUnlockSession");
        var resultType = GetRequiredType("UsbWebLauncher.Core.Authentication.ClientCertificateTokenUnlockResult");

        Assert.True(unlockerType.IsInterface);
        Assert.True(sessionType.IsInterface);
        Assert.Contains(typeof(IDisposable), sessionType.GetInterfaces());

        Assert.NotNull(unlockerType.GetMethod("UnlockAsync"));
        Assert.NotNull(resultType.GetProperty("Status"));
        Assert.NotNull(resultType.GetProperty("Session"));
        Assert.NotNull(resultType.GetProperty("ProviderErrorCode"));
    }

    [Fact]
    public void Contracts_FailureDiagnosticsPreserveOriginalRecordAndFactorySignatures()
    {
        var unlockResultType = typeof(ClientCertificateTokenUnlockResult);
        var setupResultType = typeof(ClientCertificateSetupValidationResult);

        Assert.Contains(
            unlockResultType.GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name == "Deconstruct" && method.GetParameters().Length == 3);
        Assert.Contains(
            setupResultType.GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name == "Deconstruct" && method.GetParameters().Length == 2);
        Assert.Contains(
            unlockResultType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            method => method.Name == "Failed" && method.GetParameters().Length == 2);
    }

    [Fact]
    public void Contracts_DefineBrowserClientCertificateRequestHandler()
    {
        var handlerType = GetRequiredType("UsbWebLauncher.Core.Authentication.IBrowserClientCertificateRequestHandler");
        var requestType = GetRequiredType("UsbWebLauncher.Core.Authentication.ClientCertificateRequestSnapshot");
        var matchResultType = GetRequiredType("UsbWebLauncher.Core.Authentication.ClientCertificateRequestMatchResult");

        var handleAsync = handlerType.GetMethod("HandleAsync");

        Assert.True(handlerType.IsInterface);
        Assert.NotNull(handleAsync);
        Assert.Equal(requestType, handleAsync!.GetParameters()[0].ParameterType);
        Assert.Contains(matchResultType.Name, handleAsync.ReturnType.FullName);
    }

    [Fact]
    public void Contracts_RedactPinTextRepresentation()
    {
        using var pin = CreatePin(['1', '2', '3', '4', '5', '6', '7', '8']);

        Assert.Equal("[REDACTED]", pin.ToString());
    }

    [Fact]
    public void Contracts_ClientCertificatePinCopiesSourceCharsIntoOwnedBuffer()
    {
        var source = new[] { '1', '3', '5', '7', '9' };
        using var pin = CreatePin(source);

        source[0] = 'X';
        source[1] = 'X';

        Assert.Equal(
            ['1', '3', '5', '7', '9'],
            pin.AsMemory().ToArray());

        Array.Clear(source, 0, source.Length);
    }

    [Fact]
    public void Contracts_ClientCertificatePinClearsOwnedBufferWhenDisposed()
    {
        var pin = CreatePin(['2', '4', '6', '8']);
        pin.Dispose();

        Assert.Equal(
            ['\0', '\0', '\0', '\0'],
            pin.AsMemory().ToArray());
    }

    [Fact]
    public void Contracts_ClientCertificatePinDoesNotExposePublicStringIngress()
    {
        var pinType = GetRequiredType("UsbWebLauncher.Core.Authentication.ClientCertificatePin");

        Assert.DoesNotContain(
            pinType.GetConstructors(BindingFlags.Public | BindingFlags.Instance),
            constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)));

        Assert.DoesNotContain(
            pinType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)));

        Assert.Contains(
            pinType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            method => method.Name == "CopyOf" &&
                      method.GetParameters() is [{ ParameterType: var parameterType }] &&
                      parameterType == typeof(ReadOnlyMemory<char>));
    }

    [Fact]
    public void Contracts_KeepAuthenticationBoundaryFreeFromUiAndLegacyCryptoLeaks()
    {
        var coreAssembly = typeof(LauncherProfile).Assembly;
        var authenticationTypes = coreAssembly
            .GetTypes()
            .Where(type => type.IsPublic && type.Namespace == "UsbWebLauncher.Core.Authentication")
            .ToArray();

        Assert.NotEmpty(authenticationTypes);

        foreach (var type in authenticationTypes)
        {
            AssertSignatureTypeIsPortable(type);

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                AssertSignatureTypeIsPortable(property.PropertyType);
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertSignatureTypeIsPortable(method.ReturnType);

                foreach (var parameter in method.GetParameters())
                {
                    AssertSignatureTypeIsPortable(parameter.ParameterType);
                }
            }
        }

        AssertNoLegacyCryptoAssemblyInterop(coreAssembly);
    }

    private static void AssertSignatureTypeIsPortable(Type type)
    {
        foreach (var inspectedType in ExpandSignatureTypes(type))
        {
            var fullName = inspectedType.FullName ?? inspectedType.Name;

            Assert.DoesNotContain("Microsoft.Web.WebView2", fullName, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Windows", fullName, StringComparison.Ordinal);
            Assert.DoesNotContain("Windows.Win32", fullName, StringComparison.Ordinal);
            Assert.DoesNotContain("Credential", fullName, StringComparison.OrdinalIgnoreCase);
            AssertDoesNotContainLegacyCryptoIndicator(fullName);
        }
    }

    private static void AssertNoLegacyCryptoAssemblyInterop(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            AssertDoesNotContainLegacyCryptoIndicator(type.FullName ?? type.Name);
            AssertDoesNotContainLegacyCryptoIndicator(type.Name);

            foreach (var attribute in type.CustomAttributes)
            {
                AssertAttributeDoesNotLeakLegacyCrypto(attribute);
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertDoesNotContainLegacyCryptoIndicator(property.Name);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertDoesNotContainLegacyCryptoIndicator(field.Name);
            }

            foreach (var @event in type.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertDoesNotContainLegacyCryptoIndicator(@event.Name);
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertDoesNotContainLegacyCryptoIndicator(method.Name);

                foreach (var attribute in method.CustomAttributes)
                {
                    AssertAttributeDoesNotLeakLegacyCrypto(attribute);
                }
            }
        }
    }

    private static void AssertAttributeDoesNotLeakLegacyCrypto(CustomAttributeData attribute)
    {
        var attributeName = attribute.AttributeType.FullName ?? attribute.AttributeType.Name;

        Assert.DoesNotContain("DllImportAttribute", attributeName, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryImportAttribute", attributeName, StringComparison.Ordinal);

        foreach (var constructorArgument in attribute.ConstructorArguments)
        {
            if (constructorArgument.ArgumentType == typeof(string) && constructorArgument.Value is string text)
            {
                AssertDoesNotContainLegacyCryptoIndicator(text);
            }
        }

        foreach (var namedArgument in attribute.NamedArguments)
        {
            if (namedArgument.TypedValue.ArgumentType == typeof(string) &&
                namedArgument.TypedValue.Value is string text)
            {
                AssertDoesNotContainLegacyCryptoIndicator(text);
            }
        }
    }

    private static IEnumerable<Type> ExpandSignatureTypes(Type type)
    {
        if (type.IsByRef || type.IsPointer)
        {
            yield return type.GetElementType()!;
            yield break;
        }

        yield return type;

        if (type.IsGenericType)
        {
            foreach (var genericArgument in type.GetGenericArguments())
            {
                foreach (var expanded in ExpandSignatureTypes(genericArgument))
                {
                    yield return expanded;
                }
            }
        }
    }

    private static Type GetRequiredType(string fullName)
    {
        var type = typeof(LauncherProfile).Assembly.GetType(fullName);
        Assert.NotNull(type);
        return type!;
    }

    private static void AssertDoesNotContainLegacyCryptoIndicator(string text)
    {
        foreach (var indicator in new[]
                 {
                     "Advapi32",
                     "CryptAcquireContext",
                     "CryptSetProvParam",
                     "CryptGetUserKey",
                     "CryptReleaseContext",
                     "HCRYPTPROV",
                     "HCRYPTKEY"
                 })
        {
            Assert.DoesNotContain(indicator, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ClientCertificatePin CreatePin(char[] source) =>
        ClientCertificatePin.CopyOf(source.AsMemory());
}
