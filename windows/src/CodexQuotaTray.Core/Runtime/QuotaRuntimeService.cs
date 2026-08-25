using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Protocol;
using CodexQuotaTray.Core.TokenUsage;
using System.Runtime.ExceptionServices;
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
    private static readonly TimeSpan CacheHeartbeat = TimeSpan.FromMinutes(5);

    private readonly ICodexAppServerClientFactory clientFactory;
    private readonly Func<QuotaDataSource, ICodexAppServerClientFactory> clientFactoryResolver;
    private readonly SettingsService settingsService;
    private readonly PreviewPersistence persistence;
    private readonly IQuotaNotificationSink notificationSink;
    private readonly QuotaViewProjector projector;
    private readonly TimeProvider timeProvider;
    private readonly RefreshCoordinator coordinator = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private readonly SemaphoreSlim clientLifecycleGate = new(1, 1);
    private readonly SemaphoreSlim generationCommitGate = new(1, 1);
    private readonly Channel<SnapshotWork> snapshotQueue = Channel.CreateUnbounded<SnapshotWork>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly Channel<AlertEvaluationWork> alertEvaluationQueue = Channel.CreateUnbounded<AlertEvaluationWork>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly List<Task> retiredNotificationTasks = [];
    private readonly object pendingRefreshGate = new();
    private ICodexAppServerClient? client;
    private CancellationTokenSource? clientLifetime;
    private long clientGeneration;
    private RateLimitsReadResult? latestProtocol;
    private NormalizedQuotaSnapshot? latestNormalized;
    private QuotaLanSnapshot? latestLanQuotaSnapshot;
    private AlertStateDocument? alertState;
    private QuotaCacheDocument? lastPersistedCache;
    private Task? notificationTask;
    private Task? schedulerTask;
    private Task? snapshotApplyTask;
    private Task? alertEvaluationTask;
    private Task? pendingRefreshTask;
    private AppUiState current = ConnectingState();
    private CodexDiagnosticSnapshot lastDiagnostics = new();
    private CodexClientErrorKind? lastError;
    private DateTimeOffset? lastAttemptUtc;
    private bool initialized;
    private bool disposed;
    private long lastAppliedClientGeneration;
    private long lastAppliedIngressSequence;
    private bool lastAppliedHadIngressSequence;
    private DateTimeOffset? lastAppliedSuccessUtc;
    private long nextFallbackIngressSequence;

    private sealed record SnapshotWork(
        long ClientGeneration,
        long IngressSequence,
        bool HasIngressSequence,
        RateLimitsReadResult? Snapshot,
        RateLimitsUpdatedNotification? Notification,
        CancellationToken CancellationToken,
        TaskCompletionSource<bool> Completion);

    private sealed record AlertEvaluationWork(
        NormalizedQuotaSnapshot? Snapshot,
        long Generation,
        CancellationToken CancellationToken,
        TaskCompletionSource<bool> Completion);

    private sealed record ClientLease(ICodexAppServerClient Client, long Generation);

    private sealed record DetachedClient(
        ICodexAppServerClient Client,
        Task? NotificationTask,
        CancellationTokenSource? NotificationLifetime);

    public QuotaRuntimeService(
        ICodexAppServerClientFactory clientFactory,
        SettingsService settingsService,
        PreviewPersistence persistence,
        IQuotaNotificationSink? notificationSink = null,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? timeZone = null,
        Func<QuotaDataSource, ICodexAppServerClientFactory>? clientFactoryResolver = null)
    {
        this.clientFactory = clientFactory;
        this.clientFactoryResolver = clientFactoryResolver ?? (_ => clientFactory);
        this.settingsService = settingsService;
        this.persistence = persistence;
        this.notificationSink = notificationSink ?? new NullQuotaNotificationSink();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        projector = new QuotaViewProjector(this.timeProvider, timeZone ?? TimeZoneInfo.Local);
    }

    public event EventHandler<AppUiState>? StateChanged;

    // Test-only synchronization point: this fires after the coordinator has
    // applied the result and settled the current refresh, before any handoff
    // is started by the worker loop.
    internal event Action<bool, RefreshReason?>? RefreshSettled;

    public AppSettings Settings { get; private set; } = AppSettings.Defaults;

    /// <summary>
    /// Returns only the last successful normalized quota snapshot for the paired-phone
    /// LAN endpoint. This never starts, restarts, or otherwise drives the Codex runtime.
    /// </summary>
    public QuotaLanSnapshot? GetLastSuccessfulLanQuotaSnapshot() =>
        Volatile.Read(ref latestLanQuotaSnapshot);

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

    public async ValueTask RequestAsync(RefreshReason reason, CancellationToken cancellationToken = default) =>
        await RequestCoreAsync(reason, cancellationToken, calledFromNotificationLoop: false, bypassIngressBarrier: false)
            .ConfigureAwait(false);

    private async Task RequestCoreAsync(
        RefreshReason reason,
        CancellationToken cancellationToken,
        bool calledFromNotificationLoop,
        bool bypassIngressBarrier)
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

        if (calledFromNotificationLoop)
        {
            var succeeded = await RefreshCoreAsync(
                cancellationToken,
                calledFromNotificationLoop: true,
                bypassIngressBarrier: bypassIngressBarrier).ConfigureAwait(false);
            var handoffReason = CompleteRefresh(succeeded);
            if (handoffReason is { } pendingReason)
            {
                StartPendingRefresh(pendingReason);
            }

            return;
        }

        await RunRefreshWorkerAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunRefreshWorkerAsync(RefreshReason reason, CancellationToken cancellationToken)
    {
        var next = (RefreshReason?)reason;
        while (next is not null)
        {
            if (!ShouldRequest(next.Value))
            {
                next = coordinator.AbandonCurrentAndContinue();
                continue;
            }

            var succeeded = await RefreshCoreAsync(
                cancellationToken,
                calledFromNotificationLoop: false,
                bypassIngressBarrier: false).ConfigureAwait(false);
            next = CompleteRefresh(succeeded);
        }
    }

    private RefreshReason? CompleteRefresh(bool succeeded)
    {
        var handoffReason = coordinator.CompleteAndHandoff(succeeded, timeProvider.GetUtcNow());
        RefreshSettled?.Invoke(succeeded, handoffReason);
        return handoffReason;
    }

    private void StartPendingRefresh(RefreshReason reason)
    {
        Task task;
        lock (pendingRefreshGate)
        {
            if (disposed)
            {
                return;
            }

            task = RunPendingRefreshAsync(reason);
            pendingRefreshTask = task;
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (pendingRefreshGate)
                {
                    if (ReferenceEquals(pendingRefreshTask, completed))
                    {
                        pendingRefreshTask = null;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunPendingRefreshAsync(RefreshReason reason)
    {
        try
        {
            await RunRefreshWorkerAsync(reason, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (disposed)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine($"Pending quota refresh ended with error: {error.GetType().Name}");
            var next = coordinator.AbandonCurrentAndContinue();
            if (next is { } nextReason && !disposed)
            {
                await RunRefreshWorkerAsync(nextReason, lifetime.Token).ConfigureAwait(false);
            }
        }
    }

    private bool ShouldRequest(RefreshReason reason)
    {
        if (reason == RefreshReason.Manual)
        {
            return true;
        }

        if (reason == RefreshReason.CardOpened)
        {
            if (!Settings.RefreshOnPanelOpen)
            {
                return false;
            }
        }

        if (reason == RefreshReason.NetworkRestored)
        {
            if (!Settings.RefreshOnNetworkRestore)
            {
                return false;
            }
        }

        if (Settings.RefreshMode == RefreshMode.ManualOnly
            && reason is not (RefreshReason.CardOpened or RefreshReason.NetworkRestored))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (reason != RefreshReason.RateLimitNotification
            && lastAttemptUtc is { } attempt
            && now - attempt < TimeSpan.FromSeconds(10))
        {
            return false;
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
        settings = SettingsService.Normalize(settings);
        var previous = Settings;
        var sourceChanged = previous.QuotaDataSource != settings.QuotaDataSource;
        await settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        Settings = settings with { Notifications = settings.EffectiveNotifications };
        coordinator.SetMode(Settings.RefreshMode);
        if (previous.PersistQuotaCache && !Settings.PersistQuotaCache)
        {
            await persistence.ClearQuotaCacheAsync(previous.QuotaDataSource).ConfigureAwait(false);
            lastPersistedCache = null;
        }

        if (previous.PersistTokenUsageCache && !Settings.PersistTokenUsageCache)
        {
            await persistence.ClearTokenUsageCacheAsync(previous.TokenUsageDataSource).ConfigureAwait(false);
        }

        if (sourceChanged)
        {
            await SwitchQuotaDataSourceAsync(Settings.QuotaDataSource, cancellationToken).ConfigureAwait(false);
        }

        if (latestNormalized is not null)
        {
            SetCurrent(projector.Project(
                latestNormalized,
                coordinator.LastSuccessUtc ?? timeProvider.GetUtcNow(),
                Settings.ShowRemainingPercent,
                Settings.Use24HourTime));
            QueueAlertEvaluation(
                latestNormalized,
                Volatile.Read(ref clientGeneration),
                cancellationToken);
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

        if (sourceChanged && (Settings.RefreshMode != RefreshMode.ManualOnly || client is not null))
        {
            await RequestAsync(RefreshReason.Manual, cancellationToken).ConfigureAwait(false);
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
            alertEvaluationTask = AlertEvaluationLoopAsync(lifetime.Token);
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

    private async Task<ClientLease> EnsureClientAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task[] retiredToObserve;
            await clientLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (client is not null)
                {
                    return new ClientLease(client, clientGeneration);
                }

                retiredToObserve = TakeRetiredNotificationTasks();
            }
            finally
            {
                clientLifecycleGate.Release();
            }

            foreach (var retired in retiredToObserve)
            {
                await ObserveTaskAsync(retired, "retired notification loop").ConfigureAwait(false);
            }

            ICodexAppServerClient? failedClient = null;
            Exception? failure = null;
            ClientLease? connected = null;
            Task[] additionalRetired;
            var commitGateHeld = false;
            var lifecycleGateHeld = false;
            await generationCommitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            commitGateHeld = true;
            try
            {
                await clientLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                lifecycleGateHeld = true;
                ObjectDisposedException.ThrowIf(disposed, this);
                if (client is not null)
                {
                    return new ClientLease(client, clientGeneration);
                }

                additionalRetired = TakeRetiredNotificationTasks();
                if (additionalRetired.Length == 0)
                {
                    var created = clientFactoryResolver(Settings.QuotaDataSource).Create();
                    var generation = ++clientGeneration;
                    latestProtocol = null;
                    generationCommitGate.Release();
                    commitGateHeld = false;
                    client = created;
                    try
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                        await created.ConnectAsync(linked.Token).ConfigureAwait(false);
                        ObjectDisposedException.ThrowIf(disposed, this);
                        clientLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        notificationTask = NotificationLoopAsync(created, generation, clientLifetime.Token);
                        connected = new ClientLease(created, generation);
                    }
                    catch (Exception error)
                    {
                        if (ReferenceEquals(client, created) && clientGeneration == generation)
                        {
                            client = null;
                            notificationTask = null;
                        }

                        lastDiagnostics = created.Diagnostics;
                        failedClient = created;
                        failure = error;
                    }
                }
            }
            finally
            {
                if (lifecycleGateHeld)
                {
                    clientLifecycleGate.Release();
                }

                if (commitGateHeld)
                {
                    generationCommitGate.Release();
                }
            }

            if (additionalRetired.Length != 0)
            {
                foreach (var retired in additionalRetired)
                {
                    await ObserveTaskAsync(retired, "retired notification loop").ConfigureAwait(false);
                }

                continue;
            }

            if (failedClient is not null)
            {
                await DisposeDetachedClientAsync(failedClient).ConfigureAwait(false);
                ExceptionDispatchInfo.Capture(failure!).Throw();
            }

            return connected!;
        }
    }

    private async Task<bool> RefreshCoreAsync(
        CancellationToken cancellationToken,
        bool calledFromNotificationLoop,
        bool bypassIngressBarrier)
    {
        lastAttemptUtc = timeProvider.GetUtcNow();
        SetCurrent(current with
        {
            StatusText = client is null ? "正在连接 Codex…" : "正在刷新…",
            StatusTone = StatusTone.Refreshing,
            IsRefreshing = true,
        });
        ClientLease? lease = null;
        try
        {
            lease = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
            var result = bypassIngressBarrier
                ? await lease.Client.ReadRateLimitsForRecoveryAsync(cancellationToken).ConfigureAwait(false)
                : await lease.Client.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
            await EnqueueSnapshotAsync(result, lease, cancellationToken).ConfigureAwait(false);
            lastError = null;
            return true;
        }
        catch (CodexClientException error)
        {
            if (lease is null || IsCurrentClient(lease))
            {
                lastError = error.Kind;
                lastDiagnostics = lease?.Client.Diagnostics ?? lastDiagnostics;
                SetCurrent(FailureState(current, error.Kind));
            }

            if (lease is not null && RequiresReconnect(error.Kind))
            {
                await ResetClientAsync(
                    lease,
                    waitForNotificationTask: !calledFromNotificationLoop).ConfigureAwait(false);
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetCurrent(FailureState(current, CodexClientErrorKind.Cancelled));
            return false;
        }
    }

    private async Task<bool> ApplySnapshotCoreAsync(
        RateLimitsReadResult result,
        long generation,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var applyCancellation = linked.Token;
        var normalized = QuotaNormalizer.Normalize(result);
        DateTimeOffset now;
        await generationCommitGate.WaitAsync(applyCancellation).ConfigureAwait(false);
        try
        {
            if (!IsCurrentGeneration(generation))
            {
                return false;
            }

            latestProtocol = result;
            latestNormalized = normalized;
            now = timeProvider.GetUtcNow();
            lastAppliedSuccessUtc = now;
            Volatile.Write(ref latestLanQuotaSnapshot, ToLanSnapshot(normalized, now));
            lastError = null;
            SetCurrent(projector.Project(normalized, now, Settings.ShowRemainingPercent, Settings.Use24HourTime));
        }
        finally
        {
            generationCommitGate.Release();
        }

        if (Settings.PersistQuotaCache)
        {
            var cache = ToCache(normalized, now, Settings.QuotaDataSource);
            var heartbeatDue = lastPersistedCache is null
                || now - lastPersistedCache.LastSuccessUtc >= CacheHeartbeat;
            if (lastPersistedCache is null
                || heartbeatDue
                || !CacheContentEquals(lastPersistedCache, cache))
            {
                try
                {
                    var committed = await persistence.SaveQuotaCacheWithCommitAsync(
                        cache,
                        applyCancellation,
                        generationCommitGate,
                        () => IsCurrentGeneration(generation),
                        () => lastPersistedCache = cache).ConfigureAwait(false);
                    if (!committed)
                    {
                        return false;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    System.Diagnostics.Debug.WriteLine($"Quota cache write failed: {error.GetType().Name}");
                }
            }
        }

        if (!IsCurrentGeneration(generation))
        {
            return false;
        }

        // Alert delivery may wait for a platform acknowledgement. Queue it after
        // the UI/cache commit so that snapshot apply never holds the generation
        // gate while waiting for notification delivery.
        return QueueAlertEvaluation(normalized, generation, cancellationToken);
    }

    private Task<bool> EnqueueSnapshotAsync(
        RateLimitsReadResult snapshot,
        ClientLease lease,
        CancellationToken cancellationToken) =>
        EnqueueSnapshotAsync(
            lease,
            snapshot.IngressSequence,
            snapshot,
            null,
            cancellationToken);

    private Task<bool> EnqueueSnapshotAsync(
        RateLimitsUpdatedNotification notification,
        ClientLease lease,
        CancellationToken cancellationToken) =>
        EnqueueSnapshotAsync(
            lease,
            notification.IngressSequence,
            null,
            notification,
            cancellationToken);

    private Task<bool> EnqueueSnapshotAsync(
        ClientLease lease,
        long ingressSequence,
        RateLimitsReadResult? snapshot,
        RateLimitsUpdatedNotification? notification,
        CancellationToken cancellationToken)
    {
        var hasIngressSequence = ingressSequence > 0;
        var sequence = hasIngressSequence
            ? ingressSequence
            : Interlocked.Increment(ref nextFallbackIngressSequence);
        return EnqueueSnapshotAsync(
            new SnapshotWork(
                lease.Generation,
                sequence,
                hasIngressSequence,
                snapshot,
                notification,
                cancellationToken,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)),
            cancellationToken);
    }

    private async Task<bool> EnqueueSnapshotAsync(SnapshotWork work, CancellationToken cancellationToken)
    {
        if (disposed || !snapshotQueue.Writer.TryWrite(work))
        {
            work.Completion.TrySetException(new ObjectDisposedException(nameof(QuotaRuntimeService)));
        }
        else if (work.Notification is { IsOverflow: false } notification)
        {
            notification.AcknowledgeIngress();
        }

        return await work.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SnapshotApplyLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var work in snapshotQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (work.CancellationToken.IsCancellationRequested)
                {
                    work.Completion.TrySetCanceled(work.CancellationToken);
                    continue;
                }

                if (IsOlderSnapshot(work))
                {
                    work.Completion.TrySetResult(false);
                    continue;
                }

                try
                {
                    var requiresFullRead = false;
                    var applied = true;
                    if (work.Snapshot is not null)
                    {
                        applied = await ApplySnapshotCoreAsync(
                            work.Snapshot,
                            work.ClientGeneration,
                            work.CancellationToken).ConfigureAwait(false);
                    }
                    else if (work.Notification is not null)
                    {
                        if (work.Notification.IsOverflow)
                        {
                            using var overflowLinked = CancellationTokenSource.CreateLinkedTokenSource(
                                work.CancellationToken,
                                cancellationToken);
                            await generationCommitGate.WaitAsync(overflowLinked.Token).ConfigureAwait(false);
                            try
                            {
                                if (!IsCurrentGeneration(work.ClientGeneration))
                                {
                                    applied = false;
                                }
                                else
                                {
                                    latestProtocol = null;
                                }
                            }
                            finally
                            {
                                generationCommitGate.Release();
                            }

                            requiresFullRead = true;
                        }
                        else if (IsCurrentGeneration(work.ClientGeneration))
                        {
                            var merged = RateLimitsSnapshotMerger.Merge(latestProtocol, work.Notification);
                            requiresFullRead = merged.RequiresFullRead;
                            if (merged.Snapshot is not null)
                            {
                                applied = await ApplySnapshotCoreAsync(
                                    merged.Snapshot,
                                    work.ClientGeneration,
                                    work.CancellationToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            applied = false;
                        }
                    }

                    if (applied && IsCurrentGeneration(work.ClientGeneration))
                    {
                        MarkSnapshotApplied(work);
                    }

                    work.Completion.TrySetResult(applied && requiresFullRead);
                }
                catch (OperationCanceledException) when (
                    work.CancellationToken.IsCancellationRequested
                    || cancellationToken.IsCancellationRequested)
                {
                    work.Completion.TrySetCanceled(cancellationToken);
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

    private bool QueueAlertEvaluation(
        NormalizedQuotaSnapshot snapshot,
        long generation,
        CancellationToken cancellationToken)
    {
        if (disposed)
        {
            return false;
        }

        return alertEvaluationQueue.Writer.TryWrite(
            new AlertEvaluationWork(
                snapshot,
                generation,
                cancellationToken,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)));
    }

    /// <summary>
    /// Test-only barrier. It completes after all alert evaluations queued before
    /// the barrier have finished, including any notification acknowledgement wait.
    /// </summary>
    internal async Task WaitForAlertEvaluationsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!alertEvaluationQueue.Writer.TryWrite(
                new AlertEvaluationWork(null, 0, CancellationToken.None, completion)))
        {
            throw new ObjectDisposedException(nameof(QuotaRuntimeService));
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AlertEvaluationLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var work in alertEvaluationQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (work.Snapshot is null)
                {
                    work.Completion.TrySetResult(true);
                    continue;
                }

                if (work.CancellationToken.IsCancellationRequested)
                {
                    work.Completion.TrySetResult(false);
                    continue;
                }

                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        work.CancellationToken,
                        cancellationToken);
                    var evaluated = await EvaluateAlertsAsync(
                        work.Snapshot,
                        work.Generation,
                        linked.Token).ConfigureAwait(false);
                    work.Completion.TrySetResult(evaluated);
                }
                catch (OperationCanceledException) when (
                    work.CancellationToken.IsCancellationRequested
                    || cancellationToken.IsCancellationRequested)
                {
                    work.Completion.TrySetResult(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine($"Alert evaluation failed: {error.GetType().Name}");
                    work.Completion.TrySetResult(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            while (alertEvaluationQueue.Reader.TryRead(out var work))
            {
                work.Completion.TrySetResult(false);
            }
        }
    }

    private bool IsOlderSnapshot(SnapshotWork work) =>
        work.ClientGeneration < Volatile.Read(ref clientGeneration)
        || work.ClientGeneration < lastAppliedClientGeneration
        || (work.ClientGeneration == lastAppliedClientGeneration
            && work.HasIngressSequence
            && lastAppliedHadIngressSequence
            && work.IngressSequence < lastAppliedIngressSequence);

    private void MarkSnapshotApplied(SnapshotWork work)
    {
        if (work.ClientGeneration > lastAppliedClientGeneration)
        {
            lastAppliedClientGeneration = work.ClientGeneration;
            lastAppliedHadIngressSequence = false;
            lastAppliedIngressSequence = 0;
        }

        if (work.HasIngressSequence)
        {
            lastAppliedHadIngressSequence = true;
            lastAppliedIngressSequence = Math.Max(lastAppliedIngressSequence, work.IngressSequence);
        }
    }

    private async Task<bool> EvaluateAlertsAsync(
        NormalizedQuotaSnapshot snapshot,
        long generation,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentGeneration(generation))
        {
            return false;
        }

        var inputs = snapshot.Windows
            .Where(window => QuotaBucketPolicy.IsCanonical(window.BucketId))
            .Select(window => new AlertInput(
                window.AlertKey,
                AlertWindowName(window),
                (int)window.RemainingPercent,
                window.PercentageReliable,
                window.WindowDurationMinutes,
                window.ResetAtUtc,
                window.LegacyAlertKey))
            .ToArray();
        var resetCreditInputs = snapshot.ResetCredits.AvailableCount == 0
            ? null
            : snapshot.ResetCredits.Credits?
            .Select(credit => new ResetCreditExpiryInput(
                ResetCreditFingerprint.Create(credit),
                credit.Status,
                credit.ExpiresAtUtc,
                credit.Title,
                credit.ResetType))
            .ToArray();
        var reduction = QuotaAlertReducer.Reduce(
            alertState,
            inputs,
            Settings.EffectiveNotifications,
            resetCreditInputs,
            timeProvider.GetUtcNow());
        if (!IsCurrentGeneration(generation))
        {
            return false;
        }

        if (reduction.Alert is not null)
        {
            try
            {
                // The state is intentionally committed only after the platform
                // sink confirms delivery. A normal return is the sink's explicit
                // acknowledgement contract; a timeout/failure leaves the old
                // baseline untouched so a later evaluation can retry.
                await notificationSink.ShowAsync(reduction.Alert, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                // No alert-state write has happened yet, so there is no
                // document rollback that could overwrite another snapshot's
                // progress. The retained baseline makes this alert retryable.
                System.Diagnostics.Debug.WriteLine($"Quota notification delivery failed: {error.GetType().Name}");
                return false;
            }
        }

        if (alertState is null || !AlertStateContentEquals(alertState, reduction.State))
        {
            // A successful alert may finish after a reconnect increments the
            // client generation. Alert evaluation is serialized by its own
            // queue, so committing the acknowledged event is safe and avoids a
            // duplicate when the same snapshot is evaluated again. The commit
            // gate still serializes the short file replacement with detach.
            Func<bool> canCommit = reduction.Alert is not null
                ? static () => true
                : () => IsCurrentGeneration(generation);
            var committed = await persistence.SaveAlertStateWithCommitAsync(
                reduction.State,
                cancellationToken,
                generationCommitGate,
                () => !disposed && canCommit(),
                () => alertState = reduction.State).ConfigureAwait(false);
            if (!committed)
            {
                return false;
            }
        }

        return true;
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

    private async Task NotificationLoopAsync(
        ICodexAppServerClient source,
        long generation,
        CancellationToken cancellationToken)
    {
        var lease = new ClientLease(source, generation);
        try
        {
            await foreach (var notification in source.ReadNotificationsAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var requiresFullRead = await EnqueueSnapshotAsync(notification, lease, cancellationToken).ConfigureAwait(false);

                    if (requiresFullRead)
                    {
                        await RequestCoreAsync(
                            RefreshReason.RateLimitNotification,
                            cancellationToken,
                            calledFromNotificationLoop: true,
                            bypassIngressBarrier: notification.IsOverflow).ConfigureAwait(false);
                    }
                }
                finally
                {
                    notification.AcknowledgeIngress();
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
            await HandleNotificationFailureAsync(lease, clientError).ConfigureAwait(false);
        }
        catch (CodexClientException error)
        {
            await HandleNotificationFailureAsync(lease, error).ConfigureAwait(false);
        }
    }

    private async Task HandleNotificationFailureAsync(ClientLease lease, CodexClientException error)
    {
        if (disposed || !IsCurrentClient(lease))
        {
            return;
        }

        lastError = error.Kind;
        lastDiagnostics = lease.Client.Diagnostics;
        SetCurrent(FailureState(current, error.Kind));
        if (RequiresReconnect(error.Kind))
        {
            await ResetClientAsync(lease, waitForNotificationTask: false).ConfigureAwait(false);
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

            if (latestNormalized is not null)
            {
                QueueAlertEvaluation(
                    latestNormalized,
                    Volatile.Read(ref clientGeneration),
                    cancellationToken);
            }

            ApplyStaleState();
        }
    }

    private void ApplyStaleState()
    {
        if (current.IsRefreshing || lastError is not null || (lastAppliedSuccessUtc ?? coordinator.LastSuccessUtc) is not { } last)
        {
            return;
        }

        if (timeProvider.GetUtcNow() - last < coordinator.StaleAfter(MinimumReliableRemaining()))
        {
            return;
        }

        if (string.Equals(current.StatusText, "数据可能已过期", StringComparison.Ordinal)
            && current.Windows.All(window => window.IsStale))
        {
            return;
        }

        SetCurrent(current with
        {
            StatusText = "数据可能已过期",
            StatusTone = StatusTone.Warning,
            Windows = current.Windows.Select(window => window with { IsStale = true, Tone = QuotaTone.Unavailable }).ToArray(),
        });
    }

    private int? MinimumReliableRemaining() => latestNormalized?.Windows
        .Where(window => window.PercentageReliable)
        .Select(window => (int?)window.RemainingPercent)
        .Min();

    private async Task SwitchQuotaDataSourceAsync(
        QuotaDataSource source,
        CancellationToken cancellationToken)
    {
        SetCurrent(ConnectingState() with { StatusText = $"已切换到 {source}，正在刷新…" });
        var detached = await DetachClientAsync(expected: null).ConfigureAwait(false);
        if (detached is not null)
        {
            lastDiagnostics = detached.Client.Diagnostics;
            await DisposeDetachedClientAsync(detached.Client).ConfigureAwait(false);
            if (detached.NotificationTask is not null)
            {
                await ObserveTaskAsync(detached.NotificationTask, "source switch notification loop").ConfigureAwait(false);
            }

            detached.NotificationLifetime?.Dispose();
        }

        latestProtocol = null;
        latestNormalized = null;
        Volatile.Write(ref latestLanQuotaSnapshot, null);
        lastPersistedCache = null;
        lastAppliedSuccessUtc = null;
        lastAttemptUtc = null;
        lastError = null;
        coordinator.Release();
        await RestoreCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreCacheAsync(CancellationToken cancellationToken)
    {
        if (!Settings.PersistQuotaCache)
        {
            lastPersistedCache = null;
            return;
        }

        var cache = await persistence.LoadQuotaCacheAsync(cancellationToken, Settings.QuotaDataSource).ConfigureAwait(false);
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
            null,
            window.SourceSlot,
            window.UsedPercent,
            window.RemainingPercent,
            window.PercentageReliable,
            window.WindowDurationMinutes,
            window.ResetAtUtc,
            window.BucketId)).ToArray();
        latestNormalized = new NormalizedQuotaSnapshot(
            windows,
            cache.ResetCreditAvailableCount is null
                ? new ResetCreditViewState(
                    ResetCreditKind.Unavailable,
                    Credits: cache.ResetCreditCredits)
                : new ResetCreditViewState(
                    cache.ResetCreditAvailableCount == 0 ? ResetCreditKind.Empty : ResetCreditKind.CountOnly,
                    (int)Math.Clamp(cache.ResetCreditAvailableCount.Value, 0, int.MaxValue),
                    cache.ResetCreditEarliestExpiryUtc,
                    Credits: cache.ResetCreditCredits),
            cache.PlanType,
            0,
            cache.ResetCreditAvailableCount is not null || cache.ResetCreditCredits is not null,
            cache.ResetCreditAvailableCount,
            null);
        lastAppliedSuccessUtc = cache.LastSuccessUtc;
        Volatile.Write(ref latestLanQuotaSnapshot, ToLanSnapshot(latestNormalized, cache.LastSuccessUtc));
        SetCurrent(projector.Project(latestNormalized, cache.LastSuccessUtc, Settings.ShowRemainingPercent, Settings.Use24HourTime));
    }

    private static QuotaLanSnapshot ToLanSnapshot(NormalizedQuotaSnapshot snapshot, DateTimeOffset generatedAtUtc)
    {
        var visibleWindows = snapshot.Windows
            .Where(window => QuotaBucketPolicy.IsCanonical(window.BucketId))
            .ToArray();
        return new QuotaLanSnapshot(
            SchemaVersion: 1,
            GeneratedAtUtc: generatedAtUtc,
            PlanType: visibleWindows.Length == 0 ? null : snapshot.PlanType,
            QuotaState: visibleWindows.Length == 0 ? "zero_windows" : "available",
            Windows: visibleWindows.Select(window => new QuotaLanWindow(
                LimitId: window.LocalKey,
                LimitName: window.LimitName,
                PlanType: null,
                SourceSlot: window.SourceSlot,
                UsedPercent: window.UsedPercent,
                RemainingPercent: window.RemainingPercent,
                PercentageReliable: window.PercentageReliable,
                WindowDurationMins: window.WindowDurationMinutes,
                ResetsAt: window.ResetAtUtc?.ToUnixTimeSeconds(),
                BucketId: window.BucketId)).ToArray(),
            ResetCredits: ToLanResetCredits(snapshot));
    }

    private static QuotaLanResetCredits? ToLanResetCredits(NormalizedQuotaSnapshot snapshot) =>
        !snapshot.ResetCreditsFieldPresent
            ? null
            : new QuotaLanResetCredits(
                snapshot.ResetCredits.AvailableCount,
                snapshot.ResetCredits.Credits?.Select(credit => new QuotaLanResetCredit(
                    credit.Id,
                    credit.ResetType,
                    credit.Status,
                    credit.GrantedAtUtc?.ToUnixTimeSeconds(),
                    credit.ExpiresAtUtc?.ToUnixTimeSeconds(),
                    credit.Title,
                    credit.Description)).ToArray());

    private static QuotaCacheDocument ToCache(
        NormalizedQuotaSnapshot snapshot,
        DateTimeOffset now,
        QuotaDataSource source) => new(
        1,
        now,
        snapshot.PlanType,
        snapshot.Windows.Take(32).Select(window => new QuotaCacheWindow(
            window.SourceSlot,
            window.UsedPercent,
            window.RemainingPercent,
            window.PercentageReliable,
            window.WindowDurationMinutes,
            window.ResetAtUtc,
            window.BucketId)).ToArray(),
        snapshot.AvailableCount,
        snapshot.ResetCredits.EarliestKnownExpiry,
        snapshot.ResetCredits.Credits?
            .Select(credit => credit with { Id = null })
            .ToArray(),
        source);

    private static bool CacheContentEquals(QuotaCacheDocument left, QuotaCacheDocument right) =>
        left.FormatVersion == right.FormatVersion
        && left.Source == right.Source
        && string.Equals(left.PlanType, right.PlanType, StringComparison.Ordinal)
        && left.ResetCreditAvailableCount == right.ResetCreditAvailableCount
        && left.ResetCreditEarliestExpiryUtc == right.ResetCreditEarliestExpiryUtc
        && ((left.ResetCreditCredits is null && right.ResetCreditCredits is null)
            || (left.ResetCreditCredits is not null
                && right.ResetCreditCredits is not null
                && left.ResetCreditCredits.SequenceEqual(right.ResetCreditCredits)))
        && left.Windows.Count == right.Windows.Count
        && left.Windows.Zip(right.Windows).All(pair =>
            string.Equals(pair.First.SourceSlot, pair.Second.SourceSlot, StringComparison.Ordinal)
            && string.Equals(pair.First.BucketId, pair.Second.BucketId, StringComparison.Ordinal)
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
            || left.Windows.Count != right.Windows.Count
            || (left.ResetCredits?.Count ?? 0) != (right.ResetCredits?.Count ?? 0))
        {
            return false;
        }

        if (!left.Windows.All(entry =>
            right.Windows.TryGetValue(entry.Key, out var other)
            && entry.Value.PseudonymousKey == other.PseudonymousKey
            && entry.Value.WindowDurationMinutes == other.WindowDurationMinutes
            && entry.Value.ResetAtUtc == other.ResetAtUtc
            && entry.Value.LastReliableRemaining == other.LastReliableRemaining
            && entry.Value.HandledThresholds.SequenceEqual(other.HandledThresholds)
            && entry.Value.LastResetAlertCycleUtc == other.LastResetAlertCycleUtc
            && entry.Value.ResetAlertCycleConsumed == other.ResetAlertCycleConsumed
            && entry.Value.ResetAlertAwaitingCycleMetadata == other.ResetAlertAwaitingCycleMetadata))
        {
            return false;
        }

        return (left.ResetCredits ?? new Dictionary<string, ResetCreditAlertState>()).All(entry =>
            right.ResetCredits?.TryGetValue(entry.Key, out var other) == true
            && entry.Value == other);
    }

    private void SetCurrent(AppUiState state)
    {
        if (disposed)
        {
            return;
        }

        var next = state with { IsRefreshing = state.StatusTone == StatusTone.Refreshing, IsPrototype = false };
        if (UiStateContentEquals(current, next))
        {
            return;
        }

        current = next;
        StateChanged?.Invoke(this, current);
    }

    private static bool UiStateContentEquals(AppUiState left, AppUiState right) =>
        left.Title == right.Title
        && left.PlanBadge == right.PlanBadge
        && left.StatusText == right.StatusText
        && left.StatusTone == right.StatusTone
        && left.ResetCredits == right.ResetCredits
        && left.IsRefreshing == right.IsRefreshing
        && left.IsPrototype == right.IsPrototype
        && left.Windows.SequenceEqual(right.Windows);

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

    private bool IsCurrentClient(ClientLease lease) =>
        ReferenceEquals(Volatile.Read(ref client), lease.Client)
        && Volatile.Read(ref clientGeneration) == lease.Generation;

    private bool IsCurrentGeneration(long generation) =>
        Volatile.Read(ref clientGeneration) == generation;

    private async Task ResetClientAsync(ClientLease expected, bool waitForNotificationTask)
    {
        var detached = await DetachClientAsync(expected).ConfigureAwait(false);
        if (detached is null)
        {
            return;
        }

        lastDiagnostics = detached.Client.Diagnostics;
        await DisposeDetachedClientAsync(detached.Client).ConfigureAwait(false);
        if (waitForNotificationTask && detached.NotificationTask is not null)
        {
            await ObserveTaskAsync(detached.NotificationTask, "reset notification loop").ConfigureAwait(false);
        }

        detached.NotificationLifetime?.Dispose();
    }

    private async Task<DetachedClient?> DetachClientAsync(ClientLease? expected)
    {
        await generationCommitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await clientLifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (client is null
                    || (expected is not null
                        && (!ReferenceEquals(client, expected.Client) || clientGeneration != expected.Generation)))
                {
                    return null;
                }

                var detached = client;
                client = null;
                clientGeneration++;
                var retiredLifetime = clientLifetime;
                clientLifetime = null;
                retiredLifetime?.Cancel();
                var retiredTask = notificationTask;
                if (notificationTask is not null)
                {
                    retiredNotificationTasks.Add(notificationTask);
                    notificationTask = null;
                }

                return new DetachedClient(detached, retiredTask, retiredLifetime);
            }
            finally
            {
                clientLifecycleGate.Release();
            }
        }
        finally
        {
            generationCommitGate.Release();
        }
    }

    private Task[] TakeRetiredNotificationTasks()
    {
        var tasks = retiredNotificationTasks.ToArray();
        retiredNotificationTasks.Clear();
        return tasks;
    }

    private static async Task DisposeDetachedClientAsync(ICodexAppServerClient detached)
    {
        try
        {
            await detached.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine($"Codex client dispose failed: {error.GetType().Name}");
        }
    }

    private static async Task ObserveTaskAsync(Task task, string description)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine($"Codex {description} ended with error: {error.GetType().Name}");
        }
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
            CodexClientErrorKind.OAuthLoginRequired => "OAuth 账户未登录",
            CodexClientErrorKind.OAuthRefreshFailed => "OAuth 登录已失效",
            CodexClientErrorKind.OAuthNetwork => "OAuth 网络请求失败",
            CodexClientErrorKind.OAuthProtocol => "OAuth 响应无法解析",
            _ => "连接失败",
        };
        return previous with
        {
            StatusText = previous.Windows.Count == 0
                ? $"刷新失败：{reason} · 点击刷新重试"
                : $"刷新失败：{reason} · 显示上次数据",
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
        alertEvaluationQueue.Writer.TryComplete();
        coordinator.Release();
        var detached = await DetachClientAsync(expected: null).ConfigureAwait(false);
        if (detached is not null)
        {
            lastDiagnostics = detached.Client.Diagnostics;
            await DisposeDetachedClientAsync(detached.Client).ConfigureAwait(false);
        }

        var tasks = new HashSet<Task>();
        lock (pendingRefreshGate)
        {
            if (pendingRefreshTask is not null)
            {
                tasks.Add(pendingRefreshTask);
                pendingRefreshTask = null;
            }
        }

        await clientLifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            tasks.UnionWith(retiredNotificationTasks);
            retiredNotificationTasks.Clear();
            if (notificationTask is not null)
            {
                tasks.Add(notificationTask);
                notificationTask = null;
            }
        }
        finally
        {
            clientLifecycleGate.Release();
        }

        if (schedulerTask is not null)
        {
            tasks.Add(schedulerTask);
        }

        if (snapshotApplyTask is not null)
        {
            tasks.Add(snapshotApplyTask);
        }

        if (alertEvaluationTask is not null)
        {
            tasks.Add(alertEvaluationTask);
        }

        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine($"Codex runtime task ended with error: {error.GetType().Name}");
            }
        }

        detached?.NotificationLifetime?.Dispose();
        initializationGate.Dispose();
        clientLifecycleGate.Dispose();
        generationCommitGate.Dispose();
        lifetime.Dispose();
    }
}
