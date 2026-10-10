using UsbWebLauncher.Core.Configuration;

namespace UsbWebLauncher.Core.Authentication;

public interface IClientCertificatePinSetupService
{
    Task<bool> SaveFixedAsync(
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default);

    Task<ClientCertificateSetupValidationResult> ValidateAndSaveAsync(
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(CancellationToken cancellationToken = default);
}

public interface IClientCertificatePinSetupLifecycleObserver
{
    Task ReportPinProvisionedAsync(CancellationToken cancellationToken = default);

    Task ReportPinSetupSucceededAsync(CancellationToken cancellationToken = default);

    Task ReportPinSetupDeletedAsync(CancellationToken cancellationToken = default);
}

public sealed class ClientCertificatePinSetupService(
    ClientCertificateAuthenticationOptions authentication,
    IClientCertificatePinStore pinStore,
    IClientCertificateValidationStampStore stampStore,
    IClientCertificateTokenUnlocker unlocker,
    IClientCertificateSetupVerifier verifier,
    IClientCertificatePinSetupLifecycleObserver? lifecycleObserver = null) : IClientCertificatePinSetupService
{
    public event EventHandler<AuthenticationStateChangedEventArgs>? StateChanged;

    public AuthenticationState CurrentState { get; private set; } = new(
        AuthenticationStage.RequiresPinSetup,
        null,
        Message: "需要设置本机 PIN。");

    public async Task<bool> SaveFixedAsync(
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pin);
        try
        {
            if (pin.AsMemory().IsEmpty)
            {
                SetState(new(
                    AuthenticationStage.RequiresPinSetup,
                    null,
                    Message: "固定本机 PIN 不能为空。"));
                return false;
            }

            SetState(new(
                AuthenticationStage.RequiresPinSetup,
                null,
                Message: "正在保存固定本机凭据。"));
            if (!authentication.UsesBrowserNativeAuthentication)
            {
                await stampStore.DeleteAsync(authentication.Id, cancellationToken).ConfigureAwait(false);
            }
            await pinStore.SaveAsync(
                    authentication.CredentialTargetName,
                    pin,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!authentication.UsesBrowserNativeAuthentication)
            {
                await stampStore.SaveAsync(
                    ClientCertificateValidationStamp.CreatePending(
                        authentication.Id,
                        authentication.GetNormalizedHash()),
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            SetState(new(
                AuthenticationStage.RequiresPinSetup,
                null,
                Message: authentication.UsesBrowserNativeAuthentication
                    ? "统一 PIN 已保存，将用于银行登录。"
                    : "固定本机凭据已保存，等待自动验证。"));
            if (lifecycleObserver is not null)
            {
                await lifecycleObserver.ReportPinProvisionedAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            SetState(new(
                AuthenticationStage.Failed,
                null,
                AuthenticationErrorCategory.UnknownFailure,
                Message: "固定本机凭据保存失败。"));
            return false;
        }
        finally
        {
            pin.Dispose();
        }
    }

    public async Task<ClientCertificateSetupValidationResult> ValidateAndSaveAsync(
        ClientCertificatePin pin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (authentication.UsesBrowserNativeAuthentication)
        {
            return new(await SaveFixedAsync(pin, cancellationToken).ConfigureAwait(false)
                ? ClientCertificateSetupValidationStatus.Success
                : ClientCertificateSetupValidationStatus.UnknownFailure);
        }
        if (pin.AsMemory().IsEmpty)
        {
            pin.Dispose();
            return Complete(new(ClientCertificateSetupValidationStatus.PinRejected));
        }

        SetState(new(
            AuthenticationStage.ValidatingCompatibility,
            null,
            Message: "正在验证本机兼容性。"));

        IClientCertificateTokenUnlockSession? session = null;
        try
        {
            SetState(new(
                AuthenticationStage.UnlockingToken,
                null,
                Message: "正在解锁安全设备。"));
            ClientCertificateTokenUnlockResult unlockResult;
            try
            {
                unlockResult = await unlocker.UnlockAsync(authentication, pin, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Complete(new(ClientCertificateSetupValidationStatus.Cancelled));
            }
            catch
            {
                return Complete(new(ClientCertificateSetupValidationStatus.UnknownFailure));
            }

            if (unlockResult.Status != ClientCertificateTokenUnlockStatus.Success ||
                unlockResult.Session is null)
            {
                return Complete(new(
                    MapUnlockStatus(unlockResult.Status),
                    unlockResult.ProviderErrorCode)
                {
                    TokenUnlockFailureStage = unlockResult.FailureStage
                });
            }

            session = unlockResult.Session;
            SetState(new(
                AuthenticationStage.AwaitingCertificate,
                null,
                Message: "正在等待客户端证书验证。"));
            ClientCertificateSetupValidationResult validation;
            try
            {
                validation = await verifier.VerifyAsync(authentication, session, pin, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Complete(new(ClientCertificateSetupValidationStatus.Cancelled));
            }
            catch
            {
                return Complete(new(ClientCertificateSetupValidationStatus.UnknownFailure));
            }

            if (!validation.IsSuccess)
            {
                return Complete(validation);
            }

            try
            {
                await pinStore.SaveAsync(authentication.CredentialTargetName, pin, cancellationToken)
                    .ConfigureAwait(false);
                await stampStore.SaveAsync(
                    new(authentication.Id, authentication.GetNormalizedHash()),
                    cancellationToken).ConfigureAwait(false);
                if (lifecycleObserver is not null)
                {
                    await lifecycleObserver.ReportPinSetupSucceededAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                return Complete(validation);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RollBackAsync().ConfigureAwait(false);
                return Complete(new(ClientCertificateSetupValidationStatus.Cancelled));
            }
            catch
            {
                await RollBackAsync().ConfigureAwait(false);
                return Complete(new(ClientCertificateSetupValidationStatus.UnknownFailure));
            }
        }
        finally
        {
            try
            {
                session?.Dispose();
            }
            finally
            {
                pin.Dispose();
            }
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (!authentication.UsesBrowserNativeAuthentication)
        {
            await stampStore.DeleteAsync(authentication.Id, cancellationToken).ConfigureAwait(false);
        }
        await pinStore.DeleteAsync(authentication.CredentialTargetName, cancellationToken).ConfigureAwait(false);
        if (lifecycleObserver is not null)
        {
            await lifecycleObserver.ReportPinSetupDeletedAsync(cancellationToken).ConfigureAwait(false);
        }
        SetState(new(
            AuthenticationStage.RequiresPinSetup,
            null,
            Message: "本机 PIN 已删除，需要重新设置。"));
    }

    private ClientCertificateSetupValidationResult Complete(ClientCertificateSetupValidationResult result)
    {
        var state = result.Status switch
        {
            ClientCertificateSetupValidationStatus.Success => new AuthenticationState(
                AuthenticationStage.Ready, null, Message: "本机认证验证成功。"),
            ClientCertificateSetupValidationStatus.PinRejected => new AuthenticationState(
                AuthenticationStage.RequiresPinSetup,
                null,
                AuthenticationErrorCategory.PinRejected,
                result.PublicErrorCode,
                WithTokenDiagnostic("PIN 验证未通过，需要重新设置。", result)),
            ClientCertificateSetupValidationStatus.CertificateRejected => new AuthenticationState(
                AuthenticationStage.Incompatible,
                null,
                AuthenticationErrorCategory.CertificateMissing,
                result.PublicErrorCode,
                "目标站点未接受配置的客户端证书。"),
            ClientCertificateSetupValidationStatus.Incompatible => new AuthenticationState(
                AuthenticationStage.Incompatible,
                null,
                AuthenticationErrorCategory.Unsupported,
                result.PublicErrorCode,
                WithTokenDiagnostic("当前安全设备与内置浏览器不兼容。", result)),
            ClientCertificateSetupValidationStatus.Cancelled => new AuthenticationState(
                AuthenticationStage.RequiresPinSetup, null, Message: "已取消本机 PIN 设置。"),
            ClientCertificateSetupValidationStatus.TimedOut => new AuthenticationState(
                AuthenticationStage.Incompatible,
                null,
                AuthenticationErrorCategory.TimedOut,
                result.PublicErrorCode,
                "客户端证书验证超时，无法确认自动认证兼容性。"),
            ClientCertificateSetupValidationStatus.TlsFailed => new AuthenticationState(
                AuthenticationStage.Incompatible,
                null,
                AuthenticationErrorCategory.TlsFailed,
                result.PublicErrorCode,
                "客户端证书握手失败，无法确认自动认证兼容性。"),
            _ => new AuthenticationState(
                AuthenticationStage.Failed,
                null,
                AuthenticationErrorCategory.UnknownFailure,
                result.PublicErrorCode,
                WithTokenDiagnostic("本机认证设置失败。", result))
        };
        SetState(state);
        return result;
    }

    private void SetState(AuthenticationState state)
    {
        CurrentState = state;
        var subscribers = StateChanged;
        if (subscribers is null)
        {
            return;
        }

        var eventArgs = new AuthenticationStateChangedEventArgs(state);
        foreach (EventHandler<AuthenticationStateChangedEventArgs> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, eventArgs);
            }
            catch
            {
            }
        }
    }

    private async Task RollBackAsync()
    {
        try { await stampStore.DeleteAsync(authentication.Id, CancellationToken.None).ConfigureAwait(false); }
        catch { }
        try { await pinStore.DeleteAsync(authentication.CredentialTargetName, CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }

    private static ClientCertificateSetupValidationStatus MapUnlockStatus(
        ClientCertificateTokenUnlockStatus status) => status switch
        {
            ClientCertificateTokenUnlockStatus.PinRejected => ClientCertificateSetupValidationStatus.PinRejected,
            ClientCertificateTokenUnlockStatus.ProviderUnavailable or
            ClientCertificateTokenUnlockStatus.Unsupported => ClientCertificateSetupValidationStatus.Incompatible,
            _ => ClientCertificateSetupValidationStatus.UnknownFailure
        };

    private static string WithTokenDiagnostic(
        string message,
        ClientCertificateSetupValidationResult result)
    {
        var details = new List<string>(2);
        if (result.TokenUnlockFailureStage is { } failureStage)
        {
            details.Add($"阶段：{GetFailureStageLabel(failureStage)}");
        }

        if (result.PublicErrorCode is { } errorCode)
        {
            details.Add($"错误码：0x{unchecked((uint)errorCode):X8}");
        }

        return details.Count == 0
            ? message
            : $"{message.TrimEnd('。')}（{string.Join("；", details)}）。";
    }

    private static string GetFailureStageLabel(
        ClientCertificateTokenUnlockFailureStage failureStage) => failureStage switch
        {
            ClientCertificateTokenUnlockFailureStage.PinEncoding => "PIN 编码",
            ClientCertificateTokenUnlockFailureStage.LoadProviderModule => "加载令牌模块",
            ClientCertificateTokenUnlockFailureStage.InitializeProviderModule => "初始化令牌模块",
            ClientCertificateTokenUnlockFailureStage.EnumerateToken => "查找令牌",
            ClientCertificateTokenUnlockFailureStage.OpenTokenSession => "打开令牌会话",
            ClientCertificateTokenUnlockFailureStage.VerifyPin => "验证 PIN",
            ClientCertificateTokenUnlockFailureStage.AcquireProvider => "打开加密提供程序",
            ClientCertificateTokenUnlockFailureStage.SetPin => "设置 PIN",
            ClientCertificateTokenUnlockFailureStage.GetUserKey => "获取私钥",
            ClientCertificateTokenUnlockFailureStage.CreateHash => "创建签名摘要",
            ClientCertificateTokenUnlockFailureStage.HashData => "准备签名挑战",
            ClientCertificateTokenUnlockFailureStage.QuerySignatureLength => "查询签名长度",
            ClientCertificateTokenUnlockFailureStage.SignHash => "执行私钥签名",
            _ => "调用加密提供程序"
        };
}
