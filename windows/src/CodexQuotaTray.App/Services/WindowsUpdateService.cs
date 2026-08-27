using CodexQuotaTray.Core.Updates;

namespace CodexQuotaTray.App.Services;

internal sealed class WindowsUpdateService : IWindowsUpdateController, IAsyncDisposable, IDisposable
{
    private readonly WindowsUpdateCoordinator coordinator;
    private readonly WindowsUpdateDownloader downloader;
    private readonly WindowsUpdateInstaller installer;
    private readonly Action installerStarted;
    private readonly Action<string>? log;
    private readonly Action<Action>? dispatch;
    private readonly Func<Task>? operationEntryHook;
    private readonly SemaphoreSlim downloadGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposeGate = new();
    private readonly Lazy<Task> disposeTask;
    private readonly object progressGate = new();
    private string? preparedInstallerPath;
    private string? preparedInstallerSha256;
    private WindowsUpdateDownloadProgress downloadProgress = WindowsUpdateDownloadProgress.Idle;
    private long? expectedDownloadTotal;
    private int activeOperations;
    private Task operationDrainTask = Task.CompletedTask;
    private TaskCompletionSource? operationsDrained;
    private bool disposalStarted;

    internal WindowsUpdateService(
        WindowsUpdateCoordinator coordinator,
        WindowsUpdateDownloader downloader,
        WindowsUpdateInstaller installer,
        Action installerStarted,
        Action<string>? log = null,
        Action<Action>? dispatch = null,
        Func<Task>? operationEntryHook = null)
    {
        this.coordinator = coordinator;
        this.downloader = downloader;
        this.installer = installer;
        this.installerStarted = installerStarted;
        this.log = log;
        this.dispatch = dispatch;
        this.operationEntryHook = operationEntryHook;
        disposeTask = new Lazy<Task>(DisposeCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        coordinator.Changed += OnCoordinatorChanged;
        coordinator.UpdateAvailable += OnUpdateAvailable;
    }

    public bool IsProduction => true;

    public bool AutomaticChecksEnabled => coordinator.AutomaticChecksEnabled;

    public bool UpdateRemindersEnabled => coordinator.UpdateRemindersEnabled;

    public bool AutoLaunchInstallerAfterDownload => coordinator.AutoLaunchInstallerAfterDownload;

    public DateTimeOffset? LastAttemptUtc => coordinator.LastAttemptUtc;

    public DateTimeOffset? LastSuccessfulCheckUtc => coordinator.LastSuccessfulCheckUtc;

    public WindowsUpdateCheckResult CurrentResult => coordinator.CurrentResult;

    public WindowsUpdateDownloadProgress DownloadProgress
    {
        get
        {
            lock (progressGate)
            {
                return downloadProgress;
            }
        }
    }

    internal event EventHandler<WindowsUpdateRelease>? UpdateAvailable;

    public event EventHandler? Changed;

    public event EventHandler<WindowsUpdateDownloadProgress>? DownloadProgressChanged;

    public Task SetAutomaticChecksEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        RunCoordinatorOperationAsync(token => coordinator.SetAutomaticChecksEnabledAsync(enabled, token), cancellationToken);

    public Task SetUpdateRemindersEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        RunCoordinatorOperationAsync(token => coordinator.SetUpdateRemindersEnabledAsync(enabled, token), cancellationToken);

    public Task SetAutoLaunchInstallerAfterDownloadAsync(bool enabled, CancellationToken cancellationToken) =>
        RunCoordinatorOperationAsync(token => coordinator.SetAutoLaunchInstallerAfterDownloadAsync(enabled, token), cancellationToken);

    public Task<WindowsUpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken) =>
        RunCoordinatorOperationAsync(
            token => coordinator.CheckAsync(
                manual ? WindowsUpdateCheckReason.Manual : WindowsUpdateCheckReason.Automatic,
                token),
            cancellationToken);

    public async Task<WindowsUpdateDownloadResult> DownloadAsync(CancellationToken cancellationToken)
    {
        using var operation = AcquireOperation(cancellationToken);
        if (operationEntryHook is not null)
        {
            await operationEntryHook().ConfigureAwait(false);
        }

        if (!await downloadGate.WaitAsync(0, operation.Token).ConfigureAwait(false))
        {
            return WindowsUpdateDownloadResult.Failed("更新下载正在进行中。");
        }

        try
        {
            preparedInstallerPath = null;
            preparedInstallerSha256 = null;
            var release = CurrentResult.Release;
            if (!CurrentResult.HasUpdate || release is null)
            {
                return WindowsUpdateDownloadResult.Failed("当前没有可下载的 Windows 更新。");
            }

            expectedDownloadTotal = release.Installer.Size;
            PublishDownloadProgress(new WindowsUpdateDownloadProgress(
                WindowsUpdateDownloadPhase.Downloading,
                TotalBytes: expectedDownloadTotal));
            var progress = new DelegateProgress<WindowsUpdateDownloadProgress>(PublishDownloadProgress);
            var result = await downloader.DownloadAsync(release, progress, operation.Token).ConfigureAwait(false);
            if (result.Succeeded)
            {
                preparedInstallerPath = result.InstallerPath;
                preparedInstallerSha256 = release.InstallerSha256;
                var completed = DownloadProgress;
                PublishDownloadProgress(completed with { Phase = WindowsUpdateDownloadPhase.ReadyToInstall });
            }
            else
            {
                PublishDownloadProgress(new WindowsUpdateDownloadProgress(
                    result.WasCancelled
                        ? WindowsUpdateDownloadPhase.Cancelled
                        : WindowsUpdateDownloadPhase.Failed));
            }

            return result;
        }
        finally
        {
            downloadGate.Release();
        }
    }

    public Task<bool> InstallPreparedAsync(CancellationToken cancellationToken)
    {
        using var operation = AcquireOperation(cancellationToken);
        operation.Token.ThrowIfCancellationRequested();
        if (preparedInstallerPath is null || preparedInstallerSha256 is null)
        {
            PublishDownloadProgress(new WindowsUpdateDownloadProgress(WindowsUpdateDownloadPhase.Failed));
            return Task.FromResult(false);
        }

        var progress = DownloadProgress;
        PublishDownloadProgress(progress with { Phase = WindowsUpdateDownloadPhase.Installing });
        if (!installer.TryStart(preparedInstallerPath, preparedInstallerSha256))
        {
            preparedInstallerPath = null;
            preparedInstallerSha256 = null;
            PublishDownloadProgress(new WindowsUpdateDownloadProgress(WindowsUpdateDownloadPhase.Failed));
            return Task.FromResult(false);
        }

        preparedInstallerPath = null;
        preparedInstallerSha256 = null;
        installerStarted();
        return Task.FromResult(true);
    }

    private void OnCoordinatorChanged(object? sender, EventArgs args) => RaiseChanged();

    private void OnUpdateAvailable(object? sender, WindowsUpdateRelease release) => UpdateAvailable?.Invoke(this, release);

    private void PublishDownloadProgress(WindowsUpdateDownloadProgress value)
    {
        if (value.TotalBytes is null
            && value.Phase is WindowsUpdateDownloadPhase.Downloading or WindowsUpdateDownloadPhase.Verifying)
        {
            value = value with { TotalBytes = expectedDownloadTotal };
        }

        lock (progressGate)
        {
            downloadProgress = value;
        }

        void Raise()
        {
            DownloadProgressChanged?.Invoke(this, value);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (dispatch is not null)
        {
            dispatch(Raise);
            return;
        }

        Raise();
    }

    private void RaiseChanged()
    {
        if (dispatch is not null)
        {
            dispatch(() => Changed?.Invoke(this, EventArgs.Empty));
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class DelegateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private async Task RunCoordinatorOperationAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        using var lease = AcquireOperation(cancellationToken);
        await operation(lease.Token).ConfigureAwait(false);
    }

    private async Task<T> RunCoordinatorOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var lease = AcquireOperation(cancellationToken);
        return await operation(lease.Token).ConfigureAwait(false);
    }

    private OperationLease AcquireOperation(CancellationToken cancellationToken)
    {
        lock (disposeGate)
        {
            if (disposalStarted)
            {
                throw new ObjectDisposedException(nameof(WindowsUpdateService));
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            activeOperations++;
            return new OperationLease(this, linked);
        }
    }

    private void ReleaseOperation()
    {
        lock (disposeGate)
        {
            activeOperations--;
            if (disposalStarted && activeOperations == 0)
            {
                operationsDrained?.TrySetResult();
            }
        }
    }

    private sealed class OperationLease(
        WindowsUpdateService owner,
        CancellationTokenSource cancellation) : IDisposable
    {
        private WindowsUpdateService? currentOwner = owner;

        internal CancellationToken Token => cancellation.Token;

        public void Dispose()
        {
            var detached = Interlocked.Exchange(ref currentOwner, null);
            if (detached is null)
            {
                return;
            }

            cancellation.Dispose();
            detached.ReleaseOperation();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            if (!disposalStarted)
            {
                disposalStarted = true;
                if (activeOperations > 0)
                {
                    operationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    operationDrainTask = operationsDrained.Task;
                }
            }
        }

        return new ValueTask(disposeTask.Value);
    }

    private async Task DisposeCoreAsync()
    {
        coordinator.Changed -= OnCoordinatorChanged;
        coordinator.UpdateAvailable -= OnUpdateAvailable;
        try
        {
            lifetime.Cancel();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            log?.Invoke($"Windows update cancellation failed: {error.GetType().Name}");
        }

        await operationDrainTask.ConfigureAwait(false);
        await downloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            downloader.Dispose();
        }
        finally
        {
            downloadGate.Dispose();
            try
            {
                await coordinator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                log?.Invoke($"Windows update shutdown failed: {error.GetType().Name}");
            }
            finally
            {
                lifetime.Dispose();
            }
        }
    }
}
