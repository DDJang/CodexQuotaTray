using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Protocol;

namespace CodexQuotaTray.Core.Presentation;

public sealed class LiveQuotaStateProvider : IUiStateProvider, IDiagnosticTextProvider, IAsyncDisposable
{
    private readonly ICodexAppServerClientFactory clientFactory;
    private readonly QuotaViewProjector projector;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private ICodexAppServerClient? client;
    private AppUiState current = ConnectingState();
    private CodexDiagnosticSnapshot lastDiagnostics = new();
    private CodexClientErrorKind? lastError;
    private bool disposed;

    public LiveQuotaStateProvider(
        ICodexAppServerClientFactory clientFactory,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? timeZone = null)
    {
        this.clientFactory = clientFactory;
        projector = new QuotaViewProjector(timeProvider ?? TimeProvider.System, timeZone ?? TimeZoneInfo.Local);
    }

    public ValueTask<AppUiState> GetSnapshotAsync(CancellationToken cancellationToken) =>
        new(RefreshCoreAsync(cancellationToken));

    public ValueTask<AppUiState> RefreshAsync(CancellationToken cancellationToken) =>
        new(RefreshCoreAsync(cancellationToken));

    private async Task<AppUiState> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!await refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return current;
        }

        try
        {
            current = current with
            {
                StatusText = client is null ? "正在连接 Codex…" : "正在获取额度…",
                StatusTone = StatusTone.Refreshing,
                IsRefreshing = true,
            };
            client ??= clientFactory.Create();
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var response = await client.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
            var normalized = QuotaNormalizer.Normalize(response);
            current = projector.Project(normalized, DateTimeOffset.UtcNow);
            lastDiagnostics = client.Diagnostics with { WindowCount = normalized.Windows.Count };
            lastError = null;
            return current;
        }
        catch (CodexClientException error)
        {
            lastError = error.Kind;
            lastDiagnostics = (client?.Diagnostics ?? lastDiagnostics) with { LastError = error.Kind };
            current = FailureState(current, error.Kind);
            if (error.Kind is CodexClientErrorKind.TransportClosed
                or CodexClientErrorKind.ProcessStartFailed
                or CodexClientErrorKind.InitializeRejected
                or CodexClientErrorKind.InitializeTimeout
                or CodexClientErrorKind.Protocol)
            {
                await ResetClientAsync().ConfigureAwait(false);
            }

            return current;
        }
        catch (OperationCanceledException)
        {
            lastError = CodexClientErrorKind.Cancelled;
            current = FailureState(current, CodexClientErrorKind.Cancelled);
            return current;
        }
        catch (Exception error) when (error is IOException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            lastError = CodexClientErrorKind.ProcessStartFailed;
            current = FailureState(current, CodexClientErrorKind.ProcessStartFailed);
            await ResetClientAsync().ConfigureAwait(false);
            return current;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public string CreateDiagnosticText()
    {
        var value = client?.Diagnostics ?? lastDiagnostics;
        return string.Join(
            Environment.NewLine,
            "CodexQuotaTray: 0.4.3",
            $"Codex CLI found: {value.CliFound}",
            $"Codex CLI version: {value.CliVersion ?? "unreported"}",
            $"App Server started: {value.AppServerStarted}",
            $"initialize succeeded: {value.InitializeSucceeded}",
            $"account/rateLimits/read succeeded: {value.RateLimitsReadSucceeded}",
            $"quota window count: {current.Windows.Count}",
            $"rateLimitResetCredits field present: {value.ResetCreditsFieldPresent}",
            $"availableCount: {value.AvailableCount?.ToString() ?? "unavailable"}",
            $"credit detail count: {value.CreditDetailCount?.ToString() ?? "unavailable"}",
            $"last success UTC: {value.LastSuccessUtc?.ToString("O") ?? "never"}",
            $"last error: {lastError?.ToString() ?? value.LastError?.ToString() ?? "none"}",
            $"malformed JSON count: {value.MalformedJsonCount}",
            $"stderr observed: {value.StderrObserved}");
    }

    private static AppUiState ConnectingState() => new(
        "Codex",
        null,
        "正在连接 Codex…",
        StatusTone.Refreshing,
        [],
        new ResetCreditViewState(ResetCreditKind.Unavailable),
        IsRefreshing: true,
        IsPrototype: false);

    private static AppUiState FailureState(AppUiState previous, CodexClientErrorKind kind)
    {
        var reason = kind switch
        {
            CodexClientErrorKind.CliNotFound => "未找到 Codex CLI",
            CodexClientErrorKind.CliVersionProbeFailed => "Codex CLI 无法启动",
            CodexClientErrorKind.ProcessStartFailed => "无法启动 Codex App Server",
            CodexClientErrorKind.InitializeTimeout => "连接 Codex 超时",
            CodexClientErrorKind.MethodNotFound => "当前 App Server 不支持额度读取",
            CodexClientErrorKind.RequestTimeout => "额度请求超时",
            CodexClientErrorKind.Cancelled => "操作已取消",
            CodexClientErrorKind.TransportClosed => "Codex 连接已断开",
            CodexClientErrorKind.Protocol => "额度响应无法解析",
            CodexClientErrorKind.RemoteError => "App Server 拒绝额度请求",
            CodexClientErrorKind.InitializeRejected => "App Server 初始化失败",
            _ => "获取额度失败",
        };
        return previous with
        {
            StatusText = previous.Windows.Count == 0
                ? $"! {reason} · 点击刷新重试"
                : $"! 获取失败，显示上次数据 · {reason}",
            StatusTone = StatusTone.Error,
            IsRefreshing = false,
            IsPrototype = false,
        };
    }

    private async Task ResetClientAsync()
    {
        if (client is not null)
        {
            lastDiagnostics = client.Diagnostics;
            await client.DisposeAsync().ConfigureAwait(false);
            client = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetClientAsync().ConfigureAwait(false);
        }
        finally
        {
            refreshGate.Release();
            refreshGate.Dispose();
        }
    }
}
