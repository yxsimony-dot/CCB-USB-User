using System.Linq;
using System.Text.RegularExpressions;

namespace UsbWebLauncher.Core.Configuration;

public static partial class LauncherProfileValidator
{
    private const string PsbcAuthenticationId = "psbc-client-certificate";
    private const string PsbcCredentialTarget =
        "UsbWebLauncher/ClientCertificate/psbc-client-certificate";
    private const string PsbcNativePromptPolicyId = "psbc-uk-password-v1";

    public static ProfileValidationResult Validate(LauncherProfile? profile)
    {
        var errors = new List<ProfileValidationError>();

        if (profile is null)
        {
            errors.Add(new("profile", "配置不能为空。"));
            return new(errors);
        }

        Uri? targetUri = null;
        if (!Uri.TryCreate(profile.TargetUrl, UriKind.Absolute, out targetUri) ||
            targetUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(targetUri.Host) ||
            !string.IsNullOrEmpty(targetUri.UserInfo))
        {
            errors.Add(new("targetUrl", "目标地址必须是不含嵌入凭据的绝对 HTTPS 地址。"));
            targetUri = null;
        }

        if (profile.UsbDetectionStrategy is not ("legacyAllowlist" or "ccbUsbStor" or "psbcUsbKey"))
        {
            errors.Add(new("usbDetectionStrategy", "USB 检测策略不受支持。"));
        }

        if (profile.UsbDetectionStrategy == "legacyAllowlist")
        {
            ValidateUsbDevice(profile.UsbDevice, "usbDevice", errors);
            if (profile.AdditionalUsbDevices is not null)
            {
                for (var index = 0; index < profile.AdditionalUsbDevices.Count; index++)
                {
                    ValidateUsbDevice(
                        profile.AdditionalUsbDevices[index],
                        $"additionalUsbDevices[{index}]",
                        errors);
                }
            }
        }

        ValidateClientCertificateAuthentication(
            profile.ClientCertificateAuthentication,
            errors);
        if (profile.UsbDetectionStrategy == "psbcUsbKey")
        {
            ValidatePsbcClientCertificateAuthentication(
                profile.ClientCertificateAuthentication,
                errors);
        }

        return new(errors);
    }

    private static void ValidateClientCertificateAuthentication(
        ClientCertificateAuthenticationOptions? authentication,
        ICollection<ProfileValidationError> errors)
    {
        if (authentication is null)
        {
            return;
        }

        if (authentication.AuthenticationMode is not ("ConfiguredToken" or "BrowserNative"))
        {
            errors.Add(new("clientCertificateAuthentication.authenticationMode", "认证模式不受支持。"));
        }

        if (string.IsNullOrWhiteSpace(authentication.Id))
        {
            errors.Add(new("clientCertificateAuthentication.id", "认证配置 ID 不能为空。"));
        }

        var normalizedHost = authentication.Host is null
            ? string.Empty
            : ClientCertificateAuthenticationOptions.NormalizeHost(authentication.Host);
        if (string.IsNullOrWhiteSpace(normalizedHost) ||
            normalizedHost.Contains("://", StringComparison.Ordinal) ||
            Uri.CheckHostName(normalizedHost) == UriHostNameType.Unknown)
        {
            errors.Add(new("clientCertificateAuthentication.host", "认证主机必须是精确的 HTTPS 主机名。"));
        }

        if (authentication.Port is < 1 or > 65535)
        {
            errors.Add(new("clientCertificateAuthentication.port", "认证端口必须在 1 到 65535 之间。"));
        }

        ValidateAdditionalCertificateEndpoints(authentication, normalizedHost, errors);

        if (authentication.UsesBrowserNativeAuthentication)
        {
            if (string.IsNullOrWhiteSpace(authentication.CredentialTargetName))
            {
                errors.Add(new("clientCertificateAuthentication.credentialTargetName", "凭据目标名称不能为空。"));
            }

            if (!authentication.Enabled)
            {
                if (authentication.NativePromptContinuation is { Enabled: true })
                {
                    errors.Add(new(
                        "clientCertificateAuthentication.nativePromptContinuation.enabled",
                        "禁用的认证配置不能启用原生密码提示续接。"));
                }

                return;
            }

            if (authentication.NativePromptContinuation is not { Enabled: true })
            {
                errors.Add(new("clientCertificateAuthentication.nativePromptContinuation", "浏览器原生认证必须启用密码提示续接。"));
            }

            ValidateNativePromptContinuation(authentication.NativePromptContinuation, errors);
            return;
        }

        if (string.IsNullOrWhiteSpace(authentication.Certificate?.Sha1Fingerprint) ||
            !Sha1FingerprintPattern().IsMatch(
                ClientCertificateAuthenticationOptions.NormalizeFingerprint(authentication.Certificate.Sha1Fingerprint)))
        {
            errors.Add(new(
                "clientCertificateAuthentication.certificate.sha1Fingerprint",
                "证书 SHA-1 指纹必须是 40 位十六进制文本。"));
        }

        if (string.IsNullOrWhiteSpace(authentication.Certificate?.Subject))
        {
            errors.Add(new("clientCertificateAuthentication.certificate.subject", "证书主题不能为空。"));
        }

        if (string.IsNullOrWhiteSpace(authentication.Certificate?.Issuer))
        {
            errors.Add(new("clientCertificateAuthentication.certificate.issuer", "证书颁发者不能为空。"));
        }

        if (string.IsNullOrWhiteSpace(authentication.CryptographicProvider?.CspName))
        {
            errors.Add(new("clientCertificateAuthentication.cryptographicProvider.cspName", "CSP 名称不能为空。"));
        }

        if (authentication.CryptographicProvider?.ProviderType is null or < 1)
        {
            errors.Add(new("clientCertificateAuthentication.cryptographicProvider.providerType", "CSP 提供程序类型必须为正整数。"));
        }

        if (string.IsNullOrWhiteSpace(authentication.CryptographicProvider?.ContainerName))
        {
            errors.Add(new("clientCertificateAuthentication.cryptographicProvider.containerName", "密钥容器不能为空。"));
        }

        if (string.IsNullOrWhiteSpace(authentication.CryptographicProvider?.KeySpec) ||
            ClientCertificateAuthenticationOptions.NormalizeKeySpec(authentication.CryptographicProvider.KeySpec) is not
                ("AT_KEYEXCHANGE" or "AT_SIGNATURE"))
        {
            errors.Add(new(
                "clientCertificateAuthentication.cryptographicProvider.keySpec",
                "当前仅支持 AT_KEYEXCHANGE 或 AT_SIGNATURE。"));
        }

        if (string.IsNullOrWhiteSpace(authentication.CredentialTargetName))
        {
            errors.Add(new("clientCertificateAuthentication.credentialTargetName", "凭据目标名称不能为空。"));
        }

        ValidateTokenUnlock(authentication.TokenUnlock, errors);
        ValidateNativePromptContinuation(authentication.NativePromptContinuation, errors);
    }

    private static void ValidateAdditionalCertificateEndpoints(
        ClientCertificateAuthenticationOptions authentication,
        string normalizedPrimaryHost,
        ICollection<ProfileValidationError> errors)
    {
        var seenEndpoints = new HashSet<string>(StringComparer.Ordinal);
        if (IsValidCertificateHost(normalizedPrimaryHost) &&
            authentication.Port is >= 1 and <= 65535)
        {
            seenEndpoints.Add(CreateEndpointKey(normalizedPrimaryHost, authentication.Port));
        }

        if (authentication.AdditionalEndpoints is null)
        {
            return;
        }

        for (var index = 0; index < authentication.AdditionalEndpoints.Count; index++)
        {
            var endpoint = authentication.AdditionalEndpoints[index];
            var fieldPrefix = $"clientCertificateAuthentication.additionalEndpoints[{index}]";
            if (endpoint is null)
            {
                errors.Add(new(fieldPrefix, "Additional certificate endpoint cannot be null."));
                continue;
            }

            var normalizedHost = endpoint.Host is null
                ? string.Empty
                : ClientCertificateAuthenticationOptions.NormalizeHost(endpoint.Host);
            var isValidHost = IsValidCertificateHost(normalizedHost);
            if (!isValidHost)
            {
                errors.Add(new(
                    $"{fieldPrefix}.host",
                    "Additional certificate endpoint host must be an exact HTTPS host name."));
            }

            var isValidPort = endpoint.Port is >= 1 and <= 65535;
            if (!isValidPort)
            {
                errors.Add(new(
                    $"{fieldPrefix}.port",
                    "Additional certificate endpoint port must be between 1 and 65535."));
            }

            if (isValidHost &&
                isValidPort &&
                !seenEndpoints.Add(CreateEndpointKey(normalizedHost, endpoint.Port)))
            {
                errors.Add(new(
                    fieldPrefix,
                    "Additional certificate endpoint duplicates another authorized endpoint."));
            }
        }
    }

    private static bool IsValidCertificateHost(string normalizedHost) =>
        !string.IsNullOrWhiteSpace(normalizedHost) &&
        !normalizedHost.Contains("://", StringComparison.Ordinal) &&
        normalizedHost.IndexOfAny(['*', '/', '\\', '?', '#', '@']) < 0 &&
        Uri.CheckHostName(normalizedHost) != UriHostNameType.Unknown;

    private static void ValidatePsbcClientCertificateAuthentication(
        ClientCertificateAuthenticationOptions? authentication,
        ICollection<ProfileValidationError> errors)
    {
        if (authentication is null)
        {
            return;
        }

        if (!string.Equals(
                authentication.Id,
                PsbcAuthenticationId,
                StringComparison.Ordinal))
        {
            errors.Add(new(
                "clientCertificateAuthentication.id",
                "邮储认证必须使用独立的 psbc-client-certificate 配置 ID。"));
        }

        if (!authentication.UsesBrowserNativeAuthentication)
        {
            errors.Add(new(
                "clientCertificateAuthentication.authenticationMode",
                "邮储认证必须使用 BrowserNative 模式。"));
        }

        if (!string.Equals(
                authentication.CredentialTargetName,
                PsbcCredentialTarget,
                StringComparison.Ordinal))
        {
            errors.Add(new(
                "clientCertificateAuthentication.credentialTargetName",
                "邮储认证必须使用独立的邮储凭据目标。"));
        }

        ValidatePsbcCertificateHost(
            authentication.Host,
            "clientCertificateAuthentication.host",
            errors);
        if (authentication.AdditionalEndpoints is not null)
        {
            for (var index = 0; index < authentication.AdditionalEndpoints.Count; index++)
            {
                var endpoint = authentication.AdditionalEndpoints[index];
                if (endpoint is not null)
                {
                    ValidatePsbcCertificateHost(
                        endpoint.Host,
                        $"clientCertificateAuthentication.additionalEndpoints[{index}].host",
                        errors);
                }
            }
        }

        if (authentication.Certificate is not null)
        {
            errors.Add(new(
                "clientCertificateAuthentication.certificate",
                "邮储 BrowserNative 认证不能复用建行固定证书身份。"));
        }

        if (authentication.CryptographicProvider is not null)
        {
            errors.Add(new(
                "clientCertificateAuthentication.cryptographicProvider",
                "邮储 BrowserNative 认证不能复用建行固定加密提供程序。"));
        }

        if (authentication.TokenUnlock is not null)
        {
            errors.Add(new(
                "clientCertificateAuthentication.tokenUnlock",
                "邮储 BrowserNative 认证不能复用建行令牌解锁配置。"));
        }

        var prompt = authentication.NativePromptContinuation;
        if (prompt is null)
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation",
                "邮储认证必须保留独立的原生密码提示策略配置。"));
            return;
        }

        if (!string.Equals(
                prompt.PolicyId,
                PsbcNativePromptPolicyId,
                StringComparison.Ordinal))
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.policyId",
                "邮储认证必须使用独立的 psbc-uk-password-v1 策略。"));
        }

        if (authentication.Enabled &&
            !string.Equals(
                prompt.WindowScope,
                NativePromptWindowScopes.TrustedClientStrictTopLevel,
                StringComparison.Ordinal))
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.windowScope",
                "启用的邮储认证必须使用严格的受信客户端顶层窗口作用域。"));
        }

        if (!authentication.Enabled && prompt.Enabled)
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.enabled",
                "未取得实机证据前，禁用的邮储认证不能启用原生密码提示续接。"));
        }

        if (string.IsNullOrWhiteSpace(prompt.BrokerPipeName))
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.brokerPipeName",
                "邮储原生密码提示策略必须声明 broker 管道。"));
        }
    }

    private static void ValidatePsbcCertificateHost(
        string? host,
        string field,
        ICollection<ProfileValidationError> errors)
    {
        var normalizedHost = host is null
            ? string.Empty
            : ClientCertificateAuthenticationOptions.NormalizeHost(host);
        if (!IsValidCertificateHost(normalizedHost))
        {
            return;
        }

        if (!string.Equals(normalizedHost, "psbc.com", StringComparison.Ordinal) &&
            !normalizedHost.EndsWith(".psbc.com", StringComparison.Ordinal))
        {
            errors.Add(new(
                field,
                "邮储认证端点必须是 psbc.com 下的精确主机名。"));
        }
    }

    private static string CreateEndpointKey(string normalizedHost, int port) =>
        $"{normalizedHost}\n{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static void ValidateTokenUnlock(
        ClientCertificateTokenUnlockOptions? tokenUnlock,
        ICollection<ProfileValidationError> errors)
    {
        if (tokenUnlock is null)
        {
            return;
        }

        var method = string.IsNullOrWhiteSpace(tokenUnlock.Method)
            ? string.Empty
            : ClientCertificateAuthenticationOptions.NormalizeUnlockMethod(tokenUnlock.Method);
        if (method is not ("CRYPTOAPI" or "PKCS11"))
        {
            errors.Add(new(
                "clientCertificateAuthentication.tokenUnlock.method",
                "令牌解锁方式仅支持 CryptoApi 或 Pkcs11。"));
            return;
        }

        if (method == "CRYPTOAPI")
        {
            if (!string.IsNullOrWhiteSpace(tokenUnlock.ModulePath) ||
                !string.IsNullOrWhiteSpace(tokenUnlock.TokenSerialNumber))
            {
                errors.Add(new(
                    "clientCertificateAuthentication.tokenUnlock",
                    "CryptoApi 解锁方式不能配置 PKCS#11 模块或令牌序列号。"));
            }

            return;
        }

        if (!IsAbsoluteDllPath(tokenUnlock.ModulePath))
        {
            errors.Add(new(
                "clientCertificateAuthentication.tokenUnlock.modulePath",
                "PKCS#11 模块必须是绝对 DLL 路径。"));
        }

        if (string.IsNullOrWhiteSpace(tokenUnlock.TokenSerialNumber))
        {
            errors.Add(new(
                "clientCertificateAuthentication.tokenUnlock.tokenSerialNumber",
                "PKCS#11 令牌序列号不能为空。"));
        }
    }

    private static void ValidateNativePromptContinuation(
        ClientCertificateNativePromptOptions? nativePromptContinuation,
        ICollection<ProfileValidationError> errors)
    {
        if (nativePromptContinuation is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(nativePromptContinuation.BrokerPipeName))
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.brokerPipeName",
                "原生续接管道名称不能为空。"));
        }

        if (string.IsNullOrWhiteSpace(nativePromptContinuation.PolicyId))
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.policyId",
                "原生续接策略标识不能为空。"));
        }

        var isTopLevelProcess = string.Equals(
            nativePromptContinuation.WindowScope,
            NativePromptWindowScopes.TopLevelProcess,
            StringComparison.Ordinal);
        var isTrustedClientDescendant = string.Equals(
            nativePromptContinuation.WindowScope,
            NativePromptWindowScopes.TrustedClientDescendant,
            StringComparison.Ordinal);
        var isTrustedClientTopLevel = string.Equals(
            nativePromptContinuation.WindowScope,
            NativePromptWindowScopes.TrustedClientTopLevel,
            StringComparison.Ordinal);
        var isTrustedClientStrictTopLevel = string.Equals(
            nativePromptContinuation.WindowScope,
            NativePromptWindowScopes.TrustedClientStrictTopLevel,
            StringComparison.Ordinal);
        if (!isTopLevelProcess &&
            !isTrustedClientDescendant &&
            !isTrustedClientTopLevel &&
            !isTrustedClientStrictTopLevel)
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.windowScope",
                "原生续接窗口作用域不受支持。"));
        }

        if (isTopLevelProcess)
        {
            if (!IsAbsoluteLocalExecutablePath(nativePromptContinuation.ProcessPath))
            {
                errors.Add(new(
                    "clientCertificateAuthentication.nativePromptContinuation.processPath",
                    "顶层原生续接进程路径必须是本地绝对 .exe 路径。"));
            }

            if (string.IsNullOrWhiteSpace(nativePromptContinuation.ProcessSha256) ||
                !NativePromptSha256Pattern().IsMatch(nativePromptContinuation.ProcessSha256))
            {
                errors.Add(new(
                    "clientCertificateAuthentication.nativePromptContinuation.processSha256",
                    "顶层原生续接进程 SHA-256 必须是 64 位十六进制文本。"));
            }
        }
        else if (isTrustedClientDescendant ||
                 isTrustedClientTopLevel ||
                 isTrustedClientStrictTopLevel)
        {
            if (!string.IsNullOrEmpty(nativePromptContinuation.ProcessPath))
            {
                errors.Add(new(
                    "clientCertificateAuthentication.nativePromptContinuation.processPath",
                    "Trusted-client prompt process path must be empty."));
            }

            if (!IsExecutableFileName(nativePromptContinuation.ProcessFileName))
            {
                errors.Add(new(
                    "clientCertificateAuthentication.nativePromptContinuation.processFileName",
                    "子对话框进程文件名必须是不含路径的 .exe 文件名。"));
            }

            if (isTrustedClientStrictTopLevel &&
                (string.IsNullOrWhiteSpace(nativePromptContinuation.ProcessSha256) ||
                 !NativePromptSha256Pattern().IsMatch(nativePromptContinuation.ProcessSha256)))
            {
                errors.Add(new(
                    "clientCertificateAuthentication.nativePromptContinuation.processSha256",
                    "Strict trusted-client prompt process SHA-256 must be 64 hexadecimal characters."));
            }
            else if (!isTrustedClientStrictTopLevel &&
                     !string.IsNullOrEmpty(nativePromptContinuation.ProcessSha256) &&
                     !NativePromptSha256Pattern().IsMatch(nativePromptContinuation.ProcessSha256))
            {
                errors.Add(new(
                    "clientCertificateAuthentication.nativePromptContinuation.processSha256",
                    "可选的子对话框进程 SHA-256 必须是 64 位十六进制文本。"));
            }

            if (nativePromptContinuation.RequiredChildWindowClasses is not { Count: 0 })
            {
                errors.Add(new(
                    "clientCertificateAuthentication.nativePromptContinuation.requiredChildWindowClasses",
                    "Trusted-client prompt child-window ancestry must be empty."));
            }

            if (isTrustedClientDescendant)
            {
                ValidateTrustedClientDescendantPrompt(nativePromptContinuation, errors);
            }
            else if (isTrustedClientTopLevel)
            {
                ValidateTrustedClientTopLevelPrompt(nativePromptContinuation, errors);
            }
            else
            {
                ValidateTrustedClientStrictTopLevelPrompt(nativePromptContinuation, errors);
            }
        }

        if (!isTrustedClientTopLevel &&
            (string.IsNullOrWhiteSpace(nativePromptContinuation.PublisherCertificateThumbprint) ||
             !NativePromptThumbprintPattern().IsMatch(nativePromptContinuation.PublisherCertificateThumbprint)))
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.publisherCertificateThumbprint",
                "原生续接发布者证书指纹必须是 40 位十六进制文本。"));
        }

        if (isTrustedClientTopLevel &&
            !string.IsNullOrEmpty(nativePromptContinuation.PublisherCertificateThumbprint))
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.publisherCertificateThumbprint",
                "Trusted-client top-level prompt publisher thumbprint must be empty."));
        }

        if ((isTopLevelProcess || isTrustedClientTopLevel || isTrustedClientStrictTopLevel) &&
            string.IsNullOrWhiteSpace(nativePromptContinuation.WindowTitle))
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.windowTitle",
                "原生续接窗口标题不能为空。"));
        }

        if (string.IsNullOrWhiteSpace(nativePromptContinuation.WindowClass))
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.windowClass",
                "原生续接窗口类不能为空。"));
        }

        if (isTopLevelProcess &&
            (nativePromptContinuation.RequiredChildWindowClasses is null ||
             nativePromptContinuation.RequiredChildWindowClasses.Count == 0 ||
             nativePromptContinuation.RequiredChildWindowClasses.Any(string.IsNullOrWhiteSpace)))
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.requiredChildWindowClasses",
                "原生续接子窗口类链不能为空。"));
        }

        if (nativePromptContinuation.TimeoutMilliseconds is < 1000 or > 30000)
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.timeoutMilliseconds",
                "原生续接超时时间必须在 1000 到 30000 毫秒之间。"));
        }
    }

    private static void ValidateUsbDevice(
        UsbMatchOptions? device,
        string fieldPrefix,
        ICollection<ProfileValidationError> errors)
    {
        ValidateUsbId(device?.VendorId, $"{fieldPrefix}.vendorId", errors);
        ValidateUsbId(device?.ProductId, $"{fieldPrefix}.productId", errors);

        if (string.IsNullOrWhiteSpace(device?.BusReportedDescription))
        {
            errors.Add(new($"{fieldPrefix}.busReportedDescription", "USB 总线描述不能为空。"));
        }
    }

    private static void ValidateTrustedClientDescendantPrompt(
        ClientCertificateNativePromptOptions prompt,
        ICollection<ProfileValidationError> errors)
    {
        AddRequiredFieldError(
            prompt.TrustedRootWindowTitle,
            "trustedRootWindowTitle",
            "受信根窗口标题不能为空。",
            errors);
        AddRequiredFieldError(
            prompt.TrustedRootWindowClassPrefix,
            "trustedRootWindowClassPrefix",
            "受信根窗口类前缀不能为空。",
            errors);
        AddRequiredFieldError(
            prompt.InputWindowClass,
            "inputWindowClass",
            "密码输入控件类不能为空。",
            errors);
        AddRequiredFieldError(
            prompt.SubmitWindowClass,
            "submitWindowClass",
            "确认控件类不能为空。",
            errors);

        if (prompt.InputControlId is not > 0)
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.inputControlId",
                "密码输入控件 ID 必须大于零。"));
        }

        if (prompt.SubmitControlId is not > 0)
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.submitControlId",
                "确认控件 ID 必须大于零。"));
        }

        if (prompt.InputControlId is > 0 && prompt.InputControlId == prompt.SubmitControlId)
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.submitControlId",
                "密码输入控件和确认控件必须使用不同的控件 ID。"));
        }
    }

    private static void ValidateTrustedClientTopLevelPrompt(
        ClientCertificateNativePromptOptions prompt,
        ICollection<ProfileValidationError> errors)
    {
        AddRequiredFieldError(
            prompt.InputWindowClass,
            "inputWindowClass",
            "Trusted-client prompt input control class is required.",
            errors);
        AddRequiredFieldError(
            prompt.SubmitWindowClass,
            "submitWindowClass",
            "Trusted-client prompt submit control class is required.",
            errors);

        if (prompt.InputControlId is not > 0)
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.inputControlId",
                "Trusted-client prompt input control ID must be greater than zero."));
        }

        if (prompt.SubmitControlId is not > 0)
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.submitControlId",
                "Trusted-client prompt submit control ID must be greater than zero."));
        }

        if (prompt.InputControlId is > 0 && prompt.InputControlId == prompt.SubmitControlId)
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.submitControlId",
                "Trusted-client prompt input and submit control IDs must differ."));
        }
    }

    private static void ValidateTrustedClientStrictTopLevelPrompt(
        ClientCertificateNativePromptOptions prompt,
        ICollection<ProfileValidationError> errors)
    {
        ValidateTrustedClientTopLevelPrompt(prompt, errors);

        if (!string.IsNullOrEmpty(prompt.TrustedRootWindowTitle))
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.trustedRootWindowTitle",
                "Strict trusted-client top-level prompt root title must be empty."));
        }

        if (!string.IsNullOrEmpty(prompt.TrustedRootWindowClassPrefix))
        {
            errors.Add(new(
                "clientCertificateAuthentication.nativePromptContinuation.trustedRootWindowClassPrefix",
                "Strict trusted-client top-level prompt root class prefix must be empty."));
        }
    }

    private static void AddRequiredFieldError(
        string? value,
        string field,
        string message,
        ICollection<ProfileValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new(
                $"clientCertificateAuthentication.nativePromptContinuation.{field}",
                message));
        }
    }

    private static bool IsExecutableFileName(string? value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) &&
                   string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsAbsoluteDllPath(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   Path.IsPathFullyQualified(path) &&
                   !path.StartsWith(@"\\", StringComparison.Ordinal) &&
                   string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsAbsoluteLocalExecutablePath(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   Path.IsPathFullyQualified(path) &&
                   !path.StartsWith(@"\\", StringComparison.Ordinal) &&
                   string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static void ValidateUsbId(
        string? value,
        string field,
        ICollection<ProfileValidationError> errors)
    {
        if (value is null || !UsbIdPattern().IsMatch(value))
        {
            errors.Add(new(field, "USB VID/PID 必须是四位十六进制文本。"));
        }
    }

    [GeneratedRegex("^[0-9A-Fa-f]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsbIdPattern();

    [GeneratedRegex("^[0-9A-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha1FingerprintPattern();

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NativePromptSha256Pattern();

    [GeneratedRegex("^[0-9A-Fa-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex NativePromptThumbprintPattern();
}
