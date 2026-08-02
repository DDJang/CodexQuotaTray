using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Protocol;
using System.Threading.Channels;

namespace CodexQuotaTray.Core.Runtime;

public interface IQuotaNotificationSink
{
    Task ShowAsync(QuotaAlert alert, CancellationToken cancellationToken);
}

public sealed class NullQuotaNotificationSink : IQuotaNotificationSink
{
    public Task ShowAsync(QuotaAlert alert, CancellationToken cancellationToken) => Task.CompletedTask;
}

public interface IQuotaRuntimeControl
{
    AppSettings Settings { get; }

    event EventHandler<AppUiState>? StateChanged;

    Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken);

    ValueTask RequestAsync(RefreshReason reason, CancellationToken cancellationToken = default);
}

public sealed class QuotaRuntimeService :
    IUiStateProvider,
    IDiagnosticTextProvider,
    IQuotaRuntimeControl,
    IAsyncDisposable
{
    private readonly ICodexAppServerClientFactory clientFactory;
    private readonly SettingsService settingsService;
    private readonly PreviewPersistence persistence;
    private readonly IQuotaNotificationSink notificationSink;
    private readonly QuotaViewProjector projector;
    private readonly TimeProvider timeProvider;
    private readonly RefreshCoordinator coordinator = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private readonly Channel<SnapshotWork> snapshotQueue = Channel.CreateUnbounded<SnapshotWork>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly object snapshotQueueGate = new();
    private ICodexAppServerClient? client;
    private RateLimitsReadResult? latestProtocol;
    private NormalizedQuotaSnapshot? latestNormalized;
    private AlertStateDocument? alertState;
    private QuotaCacheDocument? lastPersistedCache;
    private Task? notificationTask;
    private Task? schedulerTask;
    private Task? snapshotApplyTask;
    private AppUiState current = ConnectingState();
    private CodexDiagnosticSnapshot lastDiagnostics = new();
    private CodexClientErrorKind? lastError;
    private DateTimeOffset? lastAttemptUtc;
    private bool initialized;
    private bool disposed;
    private long nextSnapshotSequence;

    private sealed record SnapshotWork(
        long Sequence,
        RateLimitsReadResult? Snapshot,
        RateLimitsUpdatedNotification? Notification,
        CancellationToken CancellationToken,
        TaskCompletionSource<bool> Completion);

    public QuotaRuntimeService(
        ICodexAppServerClientFactory clientFactory,
        SettingsService settingsService,
        PreviewPersistence persistence,
        IQuotaNotificationSink? notificationSink = null,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? timeZone = null)
    {
        this.clientFactory = clientFactory;
        this.settingsService = settingsService;
        this.persistence = persistence;
        this.notificationSink = notificationSink ?? new NullQuotaNotificationSink();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        projector = new QuotaViewProjector(this.timeProvider, timeZone ?? TimeZoneInfo.Local);
    }

    public event EventHandler<AppUiState>? StateChanged;

    public AppSettings Settings { get; private set; } = AppSettings.Defaults;

    public async ValueTask<AppUiState> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (Settings.RefreshMode != RefreshMode.ManualOnly)
        {
            await RequestAsync(RefreshReason.Startup, cancellationToken).ConfigureAwait(false);
        }

        return current;
    }

    public async ValueTask<AppUiState> RefreshAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await RequestAsync(RefreshReason.Manual, cancellationToken).ConfigureAwait(false);
        return current;
    }

    public async ValueTask RequestAsync(RefreshReason reason, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (!ShouldRequest(reason))
        {
            return;
        }

        var decision = coordinator.Request(reason);
        if (decision != RefreshDecision.Start)
        {
            return;
        }

        var next = (RefreshReason?)reason;
        while (next is not null)
        {
            var succeeded = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            next = coordinator.Complete(succeeded, timeProvider.GetUtcNow());
            if (next is { } pendingReason && !ShouldRequest(pendingReason))
            {
                coordinator.Release();
                next = null;
            }
        }
    }

    private bool ShouldRequest(RefreshReason reason)
    {
        if (!RefreshCoordinator.Allows(Settings.RefreshMode, reason) || reason == RefreshReason.Manual)
        {
            return reason == RefreshReason.Manual;
        }

        var now = timeProvider.GetUtcNow();
        var isPendingReevaluation = coordinator.IsInFlight
            && reason is RefreshReason.CardOpened or RefreshReason.Scheduled;
        if (!isPendingReevaluation
            && reason != RefreshReason.RateLimitNotification
            && lastAttemptUtc is { } attempt
            && now - attempt < TimeSpan.FromSeconds(10))
        {
            return false;
        }

        if (reason == RefreshReason.CardOpened && coordinator.LastSuccessUtc is { } success)
        {
            var age = now - success;
            return Settings.RefreshMode == RefreshMode.Auto
                ? age >= TimeSpan.FromMinutes(2)
                : age >= coordinator.StaleAfter(MinimumReliableRemaining());
        }

        if (reason == RefreshReason.Scheduled)
        {
            var anchor = coordinator.ConsecutiveFailures > 0 ? lastAttemptUtc : coordinator.LastSuccessUtc;
            return anchor is null
                || now - anchor >= coordinator.EffectiveInterval(MinimumReliableRemaining());
        }

        if (reason is RefreshReason.Resume or RefreshReason.NetworkRestored
            && coordinator.ConsecutiveFailures > 0
            && lastAttemptUtc is { } failedAttempt)
        {
            return now - failedAttempt >= coordinator.EffectiveInterval(MinimumReliableRemaining());
        }

        return true;
    }

    public async Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        var previous = Settings;
        Settings = settings with { Notifications = settings.EffectiveNotifications };
        coordinator.SetMode(Settings.RefreshMode);
        if (previous.PersistQuotaCache && !Settings.PersistQuotaCache)
        {
            await persistence.ClearQuotaCacheAsync().ConfigureAwait(false);
            lastPersistedCache = null;
        }

        if (latestNormalized is not null)
        {
            SetCurrent(projector.Project(
                latestNormalized,
                coordinator.LastSuccessUtc ?? timeProvider.GetUtcNow(),
                Settings.ShowRemainingPercent,
                Settings.Use24HourTime));
        }
        else
        {
            SetCurrent(current);
        }

        if (Settings.RefreshMode == RefreshMode.ManualOnly && client is null)
        {
            try
            {
                await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (CodexClientException error)
            {
                lastError = error.Kind;
                SetCurrent(FailureState(current, error.Kind));
            }
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return;
        }

        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            Settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            coordinator.SetMode(Settings.RefreshMode);
            alertState = await persistence.LoadAlertStateAsync(cancellationToken).ConfigureAwait(false);
            await RestoreCacheAsync(cancellationToken).ConfigureAwait(false);
            snapshotApplyTask = SnapshotApplyLoopAsync(lifetime.Token);
            schedulerTask = SchedulerLoopAsync(lifetime.Token);
            initialized = true;
            if (Settings.RefreshMode == RefreshMode.ManualOnly)
            {
                try
                {
                    await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (CodexClientException error)
                {
                    lastError = error.Kind;
                    SetCurrent(FailureState(current, error.Kind));
                }
            }
        }
        finally
        {
            initializationGate.Release();
        }
    }

    private async Task EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (client is not null)
        {
            return;
        }

        client = clientFactory.Create();
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            notificationTask = NotificationLoopAsync(client, lifetime.Token);
        }
        catch
        {
            lastDiagnostics = client.Diagnostics;
            await client.DisposeAsync().ConfigureAwait(false);
            client = null;
            throw;
        }
    }

    private async Task<bool> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        lastAttemptUtc = timeProvider.GetUtcNow();
        SetCurrent(current with
        {
            StatusText = client is null ? "正在连接 Codex…" : "正在获取额度…",
            StatusTone = StatusTone.Refreshing,
            IsRefreshing = true,
        });
        try
        {
            await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
            var result = await client!.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
            await EnqueueSnapshotAsync(result, cancellationToken).ConfigureAwait(false);
            lastError = null;
            return true;
        }
        catch (CodexClientException error)
        {
            lastError = error.Kind;
            lastDiagnostics = client?.Diagnostics ?? lastDiagnostics;
            SetCurrent(FailureState(current, error.Kind));
            if (RequiresReconnect(error.Kind))
            {
                await ResetClientAsync().ConfigureAwait(false);
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetCurrent(FailureState(current, CodexClientErrorKind.Cancelled));
            return false;
        }
    }

    private async Task ApplySnapshotCoreAsync(RateLimitsReadResult result, CancellationToken cancellationToken)
    {
        latestProtocol = result;
        latestNormalized = QuotaNormalizer.Normalize(result);
        var now = timeProvider.GetUtcNow();
        lastError = null;
        SetCurrent(projector.Project(latestNormalized, now, Settings.ShowRemainingPercent, Settings.Use24HourTime));
        if (Settings.PersistQuotaCache)
        {
            var cache = ToCache(latestNormalized, now);
            if (lastPersistedCache is null || !CacheContentEquals(lastPersistedCache, cache))
            {
                try
                {
                    await persistence.SaveQuotaCacheAsync(cache, cancellationToken).ConfigureAwait(false);
                    lastPersistedCache = cache;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    System.Diagnostics.Debug.WriteLine($"Quota cache write failed: {error.GetType().Name}");
                }
            }
        }

        try
        {
            await EvaluateAlertsAsync(latestNormalized, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // State is intentionally saved before notification. If persistence fails,
            // suppress the notification rather than risk a duplicate on the next run.
            System.Diagnostics.Debug.WriteLine($"Alert state write failed: {error.GetType().Name}");
        }
    }

    private Task<bool> EnqueueSnapshotAsync(
        RateLimitsReadResult snapshot,
        CancellationToken cancellationToken) =>
        EnqueueSnapshotAsync(
            new SnapshotWork(
                0,
                snapshot,
                null,
                cancellationToken,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)),
            cancellationToken);

    private Task<bool> EnqueueSnapshotAsync(
        RateLimitsUpdatedNotification notification,
        CancellationToken cancellationToken) =>
        EnqueueSnapshotAsync(
            new SnapshotWork(
                0,
                null,
                notification,
                cancellationToken,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)),
            cancellationToken);

    private async Task<bool> EnqueueSnapshotAsync(SnapshotWork work, CancellationToken cancellationToken)
    {
        lock (snapshotQueueGate)
        {
            work = work with { Sequence = ++nextSnapshotSequence };
            if (disposed || !snapshotQueue.Writer.TryWrite(work))
            {
                work.Completion.TrySetException(new ObjectDisposedException(nameof(QuotaRuntimeService)));
            }
        }

        return await work.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SnapshotApplyLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var work in snapshotQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                System.Diagnostics.Debug.Assert(work.Sequence > 0);
                if (work.CancellationToken.IsCancellationRequested)
                {
                    work.Completion.TrySetCanceled(work.CancellationToken);
                    continue;
                }

                try
                {
                    var requiresFullRead = false;
                    if (work.Snapshot is not null)
                    {
                        await ApplySnapshotCoreAsync(work.Snapshot, work.CancellationToken).ConfigureAwait(false);
                    }
                    else if (work.Notification is not null)
                    {
                        var merged = RateLimitsSnapshotMerger.Merge(latestProtocol, work.Notification);
                        requiresFullRead = merged.RequiresFullRead;
                        if (merged.Snapshot is not null)
                        {
                            await ApplySnapshotCoreAsync(merged.Snapshot, work.CancellationToken).ConfigureAwait(false);
                        }
                    }

                    work.Completion.TrySetResult(requiresFullRead);
                }
                catch (OperationCanceledException) when (work.CancellationToken.IsCancellationRequested)
                {
                    work.Completion.TrySetCanceled(work.CancellationToken);
                }
                catch (Exception error)
                {
                    work.Completion.TrySetException(error);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            while (snapshotQueue.Reader.TryRead(out var work))
            {
                work.Completion.TrySetCanceled(cancellationToken);
            }
        }
    }

    private async Task EvaluateAlertsAsync(NormalizedQuotaSnapshot snapshot, CancellationToken cancellationToken)
    {
        var inputs = snapshot.Windows.Select(window => new AlertInput(
            window.AlertKey,
            AlertWindowName(window),
            (int)window.RemainingPercent,
            window.PercentageReliable,
            window.WindowDurationMinutes,
            window.ResetAtUtc)).ToArray();
        var reduction = QuotaAlertReducer.Reduce(alertState, inputs, Settings.EffectiveNotifications);
        if (alertState is null || !AlertStateContentEquals(alertState, reduction.State))
        {
            await persistence.SaveAlertStateAsync(reduction.State, cancellationToken).ConfigureAwait(false);
        }

        alertState = reduction.State;
        if (reduction.Alert is not null)
        {
            try
            {
                await notificationSink.ShowAsync(reduction.Alert, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine($"Quota notification failed after state save: {error.GetType().Name}");
            }
        }
    }

    private static string AlertWindowName(NormalizedQuotaWindow window)
    {
        var duration = window.WindowDurationMinutes switch
        {
            300 => "5 小时额度",
            10_080 => "7 天额度",
            > 0 and var value when value % 1_440 == 0 => $"{value / 1_440} 天额度",
            > 0 and var value when value % 60 == 0 => $"{value / 60} 小时额度",
            > 0 and var value => $"{value} 分钟额度",
            _ => "额度窗口",
        };

        return string.IsNullOrWhiteSpace(window.LimitName)
            || string.Equals(window.LimitName.Trim(), "Codex", StringComparison.OrdinalIgnoreCase)
            ? duration
            : $"{window.LimitName.Trim()} · {duration}";
    }

    private async Task NotificationLoopAsync(ICodexAppServerClient source, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notification in source.ReadNotificationsAsync(cancellationToken).ConfigureAwait(false))
            {
                var requiresFullRead = await EnqueueSnapshotAsync(notification, cancellationToken).ConfigureAwait(false);

                if (requiresFullRead)
                {
                    await RequestAsync(RefreshReason.RateLimitNotification, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (disposed)
        {
        }
        catch (ChannelClosedException error)
        {
            var clientError = error.InnerException as CodexClientException
                ?? new CodexClientException(CodexClientErrorKind.TransportClosed, "App Server notification stream closed.", error);
            lastError = clientError.Kind;
            lastDiagnostics = source.Diagnostics;
            SetCurrent(FailureState(current, clientError.Kind));
            if (RequiresReconnect(clientError.Kind))
            {
                await ResetClientAsync().ConfigureAwait(false);
            }
        }
        catch (CodexClientException error)
        {
            lastError = error.Kind;
            lastDiagnostics = source.Diagnostics;
            SetCurrent(FailureState(current, error.Kind));
            if (RequiresReconnect(error.Kind))
            {
                await ResetClientAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var anchor = coordinator.ConsecutiveFailures > 0 ? lastAttemptUtc : coordinator.LastSuccessUtc;
            if (anchor is null
                || timeProvider.GetUtcNow() - anchor >= coordinator.EffectiveInterval(MinimumReliableRemaining()))
            {
                await RequestAsync(RefreshReason.Scheduled, cancellationToken).ConfigureAwait(false);
            }

            ApplyStaleState();
        }
    }

    private void ApplyStaleState()
    {
        if (current.IsRefreshing || lastError is not null || coordinator.LastSuccessUtc is not { } last)
        {
            return;
        }

        if (timeProvider.GetUtcNow() - last < coordinator.StaleAfter(MinimumReliableRemaining()))
        {
            return;
        }

        if (string.Equals(current.StatusText, "⚠ 数据可能已过期", StringComparison.Ordinal)
            && current.Windows.All(window => window.IsStale))
        {
            return;
        }

        SetCurrent(current with
        {
            StatusText = "⚠ 数据可能已过期",
            StatusTone = StatusTone.Warning,
            Windows = current.Windows.Select(window => window with { IsStale = true, Tone = QuotaTone.Unavailable }).ToArray(),
        });
    }

    private int? MinimumReliableRemaining() => latestNormalized?.Windows
        .Where(window => window.PercentageReliable)
        .Select(window => (int?)window.RemainingPercent)
        .Min();

    private async Task RestoreCacheAsync(CancellationToken cancellationToken)
    {
        if (!Settings.PersistQuotaCache)
        {
            lastPersistedCache = null;
            return;
        }

        var cache = await persistence.LoadQuotaCacheAsync(cancellationToken).ConfigureAwait(false);
        if (cache is null)
        {
            lastPersistedCache = null;
            return;
        }

        lastPersistedCache = cache;
        coordinator.RestoreLastSuccess(cache.LastSuccessUtc);

        var windows = cache.Windows.Select((window, index) => new NormalizedQuotaWindow(
            $"cache:{index}",
            $"fallback:{window.SourceSlot}:{window.WindowDurationMinutes?.ToString() ?? "unknown"}:{index}",
            null,
            window.SourceSlot,
            window.UsedPercent,
            window.RemainingPercent,
            window.PercentageReliable,
            window.WindowDurationMinutes,
            window.ResetAtUtc)).ToArray();
        latestNormalized = new NormalizedQuotaSnapshot(
            windows,
            cache.ResetCreditAvailableCount is null
                ? new ResetCreditViewState(ResetCreditKind.Unavailable)
                : new ResetCreditViewState(
                    cache.ResetCreditAvailableCount == 0 ? ResetCreditKind.Empty : ResetCreditKind.CountOnly,
                    (int)Math.Clamp(cache.ResetCreditAvailableCount.Value, 0, int.MaxValue),
                    cache.ResetCreditEarliestExpiryUtc),
            cache.PlanType,
            0,
            cache.ResetCreditAvailableCount is not null,
            cache.ResetCreditAvailableCount,
            null);
        SetCurrent(projector.Project(latestNormalized, cache.LastSuccessUtc, Settings.ShowRemainingPercent, Settings.Use24HourTime));
    }

    private static QuotaCacheDocument ToCache(NormalizedQuotaSnapshot snapshot, DateTimeOffset now) => new(
        1,
        now,
        snapshot.PlanType,
        snapshot.Windows.Take(32).Select(window => new QuotaCacheWindow(
            window.SourceSlot,
            window.UsedPercent,
            window.RemainingPercent,
            window.PercentageReliable,
            window.WindowDurationMinutes,
            window.ResetAtUtc)).ToArray(),
        snapshot.AvailableCount,
        snapshot.ResetCredits.EarliestKnownExpiry);

    private static bool CacheContentEquals(QuotaCacheDocument left, QuotaCacheDocument right) =>
        left.FormatVersion == right.FormatVersion
        && string.Equals(left.PlanType, right.PlanType, StringComparison.Ordinal)
        && left.ResetCreditAvailableCount == right.ResetCreditAvailableCount
        && left.ResetCreditEarliestExpiryUtc == right.ResetCreditEarliestExpiryUtc
        && left.Windows.Count == right.Windows.Count
        && left.Windows.Zip(right.Windows).All(pair =>
            string.Equals(pair.First.SourceSlot, pair.Second.SourceSlot, StringComparison.Ordinal)
            && pair.First.UsedPercent == pair.Second.UsedPercent
            && pair.First.RemainingPercent == pair.Second.RemainingPercent
            && pair.First.PercentageReliable == pair.Second.PercentageReliable
            && pair.First.WindowDurationMinutes == pair.Second.WindowDurationMinutes
            && pair.First.ResetAtUtc == pair.Second.ResetAtUtc);

    private static bool AlertStateContentEquals(AlertStateDocument left, AlertStateDocument right)
    {
        if (left.SchemaVersion != right.SchemaVersion
            || left.ResetAlertBaselineEstablished != right.ResetAlertBaselineEstablished
            || !left.BaselineThresholds.SequenceEqual(right.BaselineThresholds)
            || left.Windows.Count != right.Windows.Count)
        {
            return false;
        }

        return left.Windows.All(entry =>
            right.Windows.TryGetValue(entry.Key, out var other)
            && entry.Value.PseudonymousKey == other.PseudonymousKey
            && entry.Value.WindowDurationMinutes == other.WindowDurationMinutes
            && entry.Value.ResetAtUtc == other.ResetAtUtc
            && entry.Value.LastReliableRemaining == other.LastReliableRemaining
            && entry.Value.HandledThresholds.SequenceEqual(other.HandledThresholds)
            && entry.Value.LastResetAlertCycleUtc == other.LastResetAlertCycleUtc);
    }

    private void SetCurrent(AppUiState state)
    {
        current = state with { IsRefreshing = state.StatusTone == StatusTone.Refreshing, IsPrototype = false };
        StateChanged?.Invoke(this, current);
    }

    public string CreateDiagnosticText()
    {
        var value = client?.Diagnostics ?? lastDiagnostics;
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
        return string.Join(
            Environment.NewLine,
            $"CodexQuotaTray WinUI: {version}",
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
            $"refresh mode: {Settings.RefreshMode}",
            $"last error: {lastError?.ToString() ?? value.LastError?.ToString() ?? "none"}",
            $"malformed JSON count: {value.MalformedJsonCount}",
            $"stderr observed: {value.StderrObserved}");
    }

    private static bool RequiresReconnect(CodexClientErrorKind kind) => kind is
        CodexClientErrorKind.TransportClosed or
        CodexClientErrorKind.ProcessStartFailed or
        CodexClientErrorKind.InitializeRejected or
        CodexClientErrorKind.InitializeTimeout or
        CodexClientErrorKind.Protocol;

    private async Task ResetClientAsync()
    {
        if (client is null)
        {
            return;
        }

        lastDiagnostics = client.Diagnostics;
        await client.DisposeAsync().ConfigureAwait(false);
        client = null;
        notificationTask = null;
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
            CodexClientErrorKind.CliVersionProbeFailed => "Codex CLI 无法启动，请安装 npm 版 Codex CLI",
            CodexClientErrorKind.ProcessStartFailed => "无法启动 Codex App Server",
            CodexClientErrorKind.InitializeTimeout or CodexClientErrorKind.RequestTimeout => "请求超时",
            CodexClientErrorKind.MethodNotFound => "当前 App Server 不支持额度读取",
            CodexClientErrorKind.TransportClosed => "Codex 连接已断开",
            CodexClientErrorKind.Cancelled => "操作已取消",
            CodexClientErrorKind.Protocol => "额度响应无法解析",
            _ => "连接失败",
        };
        return previous with
        {
            StatusText = previous.Windows.Count == 0
                ? $"! {reason} · 点击刷新重试"
                : $"! 获取失败，显示上次数据 · {reason}",
            StatusTone = StatusTone.Error,
            IsRefreshing = false,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetime.Cancel();
        snapshotQueue.Writer.TryComplete();
        coordinator.Release();
        await ResetClientAsync().ConfigureAwait(false);
        foreach (var task in new[] { notificationTask, schedulerTask, snapshotApplyTask }.Where(task => task is not null))
        {
            try
            {
                await task!.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        initializationGate.Dispose();
        lifetime.Dispose();
    }
}
