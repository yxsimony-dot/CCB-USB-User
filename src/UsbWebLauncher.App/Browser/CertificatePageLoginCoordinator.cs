using UsbWebLauncher.Core.Authentication;

namespace UsbWebLauncher.App.Browser;

public enum CertificateLoginPageState { WaitingForPage, Ready, Unsupported }
public enum CertificateLoginResult { Submitted, Unsupported, Failed }

public interface ICertificateLoginPage
{
    Task<CertificateLoginPageState> InspectAsync(CancellationToken cancellationToken);
    Task<CertificateLoginResult> TryLoginAsync(ClientCertificatePin pin, CancellationToken cancellationToken);
}

public sealed class CertificatePageLoginCoordinator : IAsyncDisposable
{
    private readonly IClientCertificatePinStore _pinStore;
    private readonly string _credentialTarget;
    private readonly ICertificateLoginPage _page;
    private readonly TimeSpan _pollingInterval;
    private readonly TimeSpan _readinessTimeout;
    private readonly CancellationToken _sessionToken;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly List<Attempt> _attempts = [];
    private Attempt? _current;
    private bool _disposed;

    public CertificatePageLoginCoordinator(IClientCertificatePinStore pinStore, string credentialTarget,
        ICertificateLoginPage page, TimeSpan? pollingInterval = null, TimeSpan? readinessTimeout = null,
        CancellationToken sessionToken = default)
    {
        _pinStore = pinStore;
        _credentialTarget = credentialTarget;
        _page = page;
        _pollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(500);
        _readinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(60);
        _sessionToken = sessionToken;
    }
    public event EventHandler? StateChanged;
    public string? Message { get; private set; }

    public void BeginAttempt()
    {
        lock (_sync)
        {
            if (_disposed || _sessionToken.IsCancellationRequested) return;
            _current?.Cancellation.Cancel();
            _current = new Attempt(_sessionToken);
            CertificateLoginDiagnostics.Write("attempt-begin");
            _attempts.Add(_current);
            Message = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task ObserveAsync()
    {
        lock (_sync)
        {
            if (_disposed || _sessionToken.IsCancellationRequested || _current is null) return Task.CompletedTask;
            return _current.Work ??= RunAsync(_current);
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _current?.Cancellation.Cancel();
            _current = null;
            CertificateLoginDiagnostics.Write("attempt-cancel");
            Message = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RunAsync(Attempt attempt)
    {
        // Do not enter the adapter synchronously while ObserveAsync publishes its task.
        await Task.Yield();
        var token = attempt.Cancellation.Token;
        var acquired = false;
        try
        {
            await _serial.WaitAsync(token);
            acquired = true;
            _sessionToken.ThrowIfCancellationRequested();
            CertificateLoginDiagnostics.Write("observe-start");
            Publish(attempt, "正在识别证书登录页…");
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(token);
            readiness.CancelAfter(_readinessTimeout);
            while (true)
            {
                readiness.Token.ThrowIfCancellationRequested();
                var state = await _page.InspectAsync(readiness.Token);
                _sessionToken.ThrowIfCancellationRequested();
                readiness.Token.ThrowIfCancellationRequested();
                if (state == CertificateLoginPageState.Unsupported)
                {
                    Publish(attempt, "当前登录控件不支持自动填写，请手动登录。");
                    return;
                }
                if (state == CertificateLoginPageState.Ready) break;
                await Task.Delay(_pollingInterval, readiness.Token);
            }

            token.ThrowIfCancellationRequested();
            using var pin = await _pinStore.ReadAsync(_credentialTarget, token);
            CertificateLoginDiagnostics.Write("credential-read", new { Available = pin is not null });
            token.ThrowIfCancellationRequested();
            _sessionToken.ThrowIfCancellationRequested();
            if (pin is null || pin.AsMemory().IsEmpty)
            {
                Publish(attempt, "未找到已保存的 PIN，请先设置 PIN，再启动业务。");
                return;
            }

            // Work is retained for this attempt before calling the adapter, so even
            // navigation events raised during submission cannot cause a second login.
            var result = await _page.TryLoginAsync(pin, token);
            CertificateLoginDiagnostics.Write("submit-result", new { Result = result.ToString() });
            token.ThrowIfCancellationRequested();
            Publish(attempt, result switch
            {
                CertificateLoginResult.Submitted => "已填写登录密码并提交登录，请查看页面结果。",
                CertificateLoginResult.Unsupported => "当前登录控件不支持自动填写，请手动登录。",
                _ => "自动登录未完成，请查看页面并手动处理；不会重复提交。"
            });
        }
        catch (OperationCanceledException)
        {
            if (!token.IsCancellationRequested && !_sessionToken.IsCancellationRequested)
                Publish(attempt, "等待登录页面超时，请手动登录；不会重复提交。");
        }
        catch (Exception exception)
        {
            CertificateLoginDiagnostics.Write("observe-exception", new { Type = exception.GetType().Name });
            // Providers and web controls may embed sensitive values in exceptions.
            Publish(attempt, "自动登录未完成，请查看页面并手动处理；不会重复提交。");
        }
        finally
        {
            if (acquired) _serial.Release();
        }
    }

    private void Publish(Attempt attempt, string message)
    {
        lock (_sync)
        {
            if (_disposed || _sessionToken.IsCancellationRequested || !ReferenceEquals(_current, attempt) || attempt.Cancellation.IsCancellationRequested) return;
            Message = message;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Attempt[] attempts;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _current = null;
            attempts = _attempts.ToArray();
            foreach (var attempt in attempts) attempt.Cancellation.Cancel();
        }
        await Task.WhenAll(attempts.Select(a => a.Work ?? Task.CompletedTask));
        foreach (var attempt in attempts) attempt.Cancellation.Dispose();
        _serial.Dispose();
    }

    private sealed class Attempt(CancellationToken sessionToken)
    {
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        public Task? Work { get; set; }
    }
}
