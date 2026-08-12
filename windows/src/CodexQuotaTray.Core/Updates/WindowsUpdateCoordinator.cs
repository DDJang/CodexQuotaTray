namespace CodexQuotaTray.Core.Updates;

public sealed class WindowsUpdateCoordinator : IAsyncDisposable
{
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);

    private readonly IWindowsUpdateReleaseProvider provider;
    private readonly IWindowsUpdateStateStore stateStore;
    private readonly SemanticVersion currentVersion;
    private readonly IUpdateClock clock;
    private readonly Action<string>? log;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private Task? loadTask;
    private Task<WindowsUpdateCheckResult>? inFlight;
    private WindowsUpdateState state = new();
    private WindowsUpdateCheckResult currentResult = WindowsUpdateCheckResult.NotChecked;
    private bool disposed;

    public WindowsUpdateCoordinator(
        IWindowsUpdateReleaseProvider provider,
        IWindowsUpdateStateStore stateStore,
        SemanticVersion currentVersion,
        IUpdateClock? clock = null,
        Action<string>? log = null)
    {
        this.provider = provider;
        this.stateStore = stateStore;
        this.currentVersion = currentVersion;
        this.clock = clock ?? new SystemUpdateClock();
        this.log = log;
    }

    public bool AutomaticChecksEnabled
    {
        get { lock (gate) return state.AutomaticChecksEnabled; }
    }

    public bool UpdateRemindersEnabled
    {
        get { lock (gate) return state.UpdateRemindersEnabled; }
    }

    public bool AutoLaunchInstallerAfterDownload
    {
        get { lock (gate) return state.AutoLaunchInstallerAfterDownload; }
    }

    public DateTimeOffset? LastAttemptUtc
    {
        get { lock (gate) return state.LastAttemptUtc; }
    }

    public DateTimeOffset? LastSuccessfulCheckUtc
    {
        get { lock (gate) return state.LastSuccessfulCheckUtc; }
    }

    public WindowsUpdateCheckResult CurrentResult
    {
        get { lock (gate) return currentResult; }
    }

    public event EventHandler? Changed;

    public event EventHandler<WindowsUpdateRelease>? UpdateAvailable;

    public async Task SetAutomaticChecksEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        WindowsUpdateState updated;
        lock (gate)
        {
            updated = state with { AutomaticChecksEnabled = enabled };
            state = updated;
            if (!enabled)
            {
                currentResult = new WindowsUpdateCheckResult(
                    WindowsUpdateCheckStatus.Disabled,
                    null,
                    null,
                    clock.UtcNow);
            }
        }

        await SaveStateAsync(updated, cancellationToken).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task SetUpdateRemindersEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        WindowsUpdateState updated;
        lock (gate)
        {
            updated = state with { UpdateRemindersEnabled = enabled };
            state = updated;
        }

        await SaveStateAsync(updated, cancellationToken).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task SetAutoLaunchInstallerAfterDownloadAsync(bool enabled, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        WindowsUpdateState updated;
        lock (gate)
        {
            updated = state with { AutoLaunchInstallerAfterDownload = enabled };
            state = updated;
        }

        await SaveStateAsync(updated, cancellationToken).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task<WindowsUpdateCheckResult> CheckAsync(
        WindowsUpdateCheckReason reason,
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        Task<WindowsUpdateCheckResult> task;
        WindowsUpdateCheckResult? immediateResult = null;
        lock (gate)
        {
            ThrowIfDisposed();
            if (inFlight is not null)
            {
                task = inFlight;
            }
            else if (reason == WindowsUpdateCheckReason.Automatic && !state.AutomaticChecksEnabled)
            {
                currentResult = new WindowsUpdateCheckResult(
                    WindowsUpdateCheckStatus.Disabled,
                    null,
                    null,
                    clock.UtcNow);
                immediateResult = currentResult;
                task = Task.FromResult(currentResult);
            }
            else if (reason == WindowsUpdateCheckReason.Automatic
                && state.LastAttemptUtc is { } lastAttempt
                && clock.UtcNow - lastAttempt < AutomaticCheckInterval)
            {
                currentResult = new WindowsUpdateCheckResult(
                    WindowsUpdateCheckStatus.Skipped,
                    currentResult.Release,
                    "自动检查已在 24 小时内执行过",
                    lastAttempt);
                immediateResult = currentResult;
                task = Task.FromResult(currentResult);
            }
            else
            {
                var updated = state with { LastAttemptUtc = clock.UtcNow };
                state = updated;
                currentResult = new WindowsUpdateCheckResult(
                    WindowsUpdateCheckStatus.Checking,
                    currentResult.Release,
                    null,
                    null);
                task = RunCheckAsync(reason, updated);
                inFlight = task;
                task.ContinueWith(
                    _ =>
                    {
                        lock (gate)
                        {
                            if (ReferenceEquals(inFlight, task))
                            {
                                inFlight = null;
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        if (immediateResult is not null)
        {
            RaiseChanged();
            return immediateResult;
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WindowsUpdateCheckResult> RunCheckAsync(
        WindowsUpdateCheckReason reason,
        WindowsUpdateState attemptState)
    {
        await SaveStateAsync(attemptState, lifetime.Token).ConfigureAwait(false);
        try
        {
            var release = await provider.GetLatestAsync(lifetime.Token).ConfigureAwait(false);
            var checkedAt = clock.UtcNow;
            WindowsUpdateCheckResult result;
            WindowsUpdateState updated;
            if (release is null)
            {
                result = new WindowsUpdateCheckResult(
                    WindowsUpdateCheckStatus.NoRelease,
                    null,
                    "当前 Release 没有有效的 Windows 安装包",
                    checkedAt);
                updated = attemptState with { LastSuccessfulCheckUtc = checkedAt };
            }
            else if (release.Version.CompareTo(currentVersion) > 0)
            {
                result = new WindowsUpdateCheckResult(
                    WindowsUpdateCheckStatus.Available,
                    release,
                    null,
                    checkedAt);
                updated = attemptState with { LastSuccessfulCheckUtc = checkedAt };
            }
            else
            {
                result = new WindowsUpdateCheckResult(
                    WindowsUpdateCheckStatus.UpToDate,
                    release,
                    null,
                    checkedAt);
                updated = attemptState with { LastSuccessfulCheckUtc = checkedAt };
            }

            var shouldNotify = reason == WindowsUpdateCheckReason.Automatic
                && result.HasUpdate
                && attemptState.UpdateRemindersEnabled
                && !string.Equals(attemptState.LastNotifiedVersion, release?.Version.ToString(), StringComparison.Ordinal);
            if (shouldNotify && release is not null)
            {
                updated = updated with { LastNotifiedVersion = release.Version.ToString() };
            }

            lock (gate)
            {
                state = updated;
                currentResult = result;
            }
            await SaveStateAsync(updated, lifetime.Token).ConfigureAwait(false);
            RaiseChanged();
            if (shouldNotify && release is not null)
            {
                UpdateAvailable?.Invoke(this, release);
            }

            return result;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            var result = new WindowsUpdateCheckResult(
                WindowsUpdateCheckStatus.Failed,
                null,
                "检查更新失败",
                clock.UtcNow);
            lock (gate)
            {
                currentResult = result;
            }
            log?.Invoke($"Windows update check failed: {error.GetType().Name}");
            RaiseChanged();
            return result;
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (gate)
        {
            loadTask ??= LoadStateAsync(cancellationToken);
            task = loadTask;
        }

        await task.ConfigureAwait(false);
    }

    private async Task LoadStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                state = loaded ?? new WindowsUpdateState();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log?.Invoke($"Windows update state load failed: {error.GetType().Name}");
        }
    }

    private async Task SaveStateAsync(WindowsUpdateState value, CancellationToken cancellationToken)
    {
        try
        {
            await stateStore.SaveAsync(value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log?.Invoke($"Windows update state save failed: {error.GetType().Name}");
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsUpdateCoordinator));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? task;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            task = inFlight;
        }

        lifetime.Cancel();
        if (task is not null)
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
                log?.Invoke($"Windows update check shutdown failed: {error.GetType().Name}");
            }
        }

        lifetime.Dispose();
    }
}
