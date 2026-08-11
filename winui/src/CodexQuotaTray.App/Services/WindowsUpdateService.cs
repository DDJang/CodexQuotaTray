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
    private readonly SemaphoreSlim downloadGate = new(1, 1);
    private string? preparedInstallerPath;
    private bool disposed;

    internal WindowsUpdateService(
        WindowsUpdateCoordinator coordinator,
        WindowsUpdateDownloader downloader,
        WindowsUpdateInstaller installer,
        Action installerStarted,
        Action<string>? log = null,
        Action<Action>? dispatch = null)
    {
        this.coordinator = coordinator;
        this.downloader = downloader;
        this.installer = installer;
        this.installerStarted = installerStarted;
        this.log = log;
        this.dispatch = dispatch;
        coordinator.Changed += OnCoordinatorChanged;
        coordinator.UpdateAvailable += OnUpdateAvailable;
    }

    public bool IsProduction => true;

    public bool AutomaticChecksEnabled => coordinator.AutomaticChecksEnabled;

    public bool UpdateRemindersEnabled => coordinator.UpdateRemindersEnabled;

    public DateTimeOffset? LastAttemptUtc => coordinator.LastAttemptUtc;

    public DateTimeOffset? LastSuccessfulCheckUtc => coordinator.LastSuccessfulCheckUtc;

    public WindowsUpdateCheckResult CurrentResult => coordinator.CurrentResult;

    internal event EventHandler<WindowsUpdateRelease>? UpdateAvailable;

    public event EventHandler? Changed;

    public Task SetAutomaticChecksEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        coordinator.SetAutomaticChecksEnabledAsync(enabled, cancellationToken);

    public Task SetUpdateRemindersEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        coordinator.SetUpdateRemindersEnabledAsync(enabled, cancellationToken);

    public Task<WindowsUpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken) =>
        coordinator.CheckAsync(
            manual ? WindowsUpdateCheckReason.Manual : WindowsUpdateCheckReason.Automatic,
            cancellationToken);

    public async Task<WindowsUpdateDownloadResult> DownloadAsync(CancellationToken cancellationToken)
    {
        if (!await downloadGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return WindowsUpdateDownloadResult.Failed("更新下载正在进行中。");
        }

        try
        {
            var release = CurrentResult.Release;
            if (!CurrentResult.HasUpdate || release is null)
            {
                return WindowsUpdateDownloadResult.Failed("当前没有可下载的 Windows 更新。");
            }

            var result = await downloader.DownloadAsync(release, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                preparedInstallerPath = result.InstallerPath;
            }

            RaiseChanged();
            return result;
        }
        finally
        {
            downloadGate.Release();
        }
    }

    public Task<bool> InstallPreparedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (preparedInstallerPath is null || !installer.TryStart(preparedInstallerPath))
        {
            return Task.FromResult(false);
        }

        installerStarted();
        return Task.FromResult(true);
    }

    private void OnCoordinatorChanged(object? sender, EventArgs args) => RaiseChanged();

    private void OnUpdateAvailable(object? sender, WindowsUpdateRelease release) => UpdateAvailable?.Invoke(this, release);

    private void RaiseChanged()
    {
        if (dispatch is not null)
        {
            dispatch(() => Changed?.Invoke(this, EventArgs.Empty));
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        coordinator.Changed -= OnCoordinatorChanged;
        coordinator.UpdateAvailable -= OnUpdateAvailable;
        downloader.Dispose();
        downloadGate.Dispose();
        if (coordinator is IAsyncDisposable)
        {
            _ = DisposeCoordinatorAsync();
        }
    }

    private async Task DisposeCoordinatorAsync()
    {
        try
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            log?.Invoke($"Windows update shutdown failed: {error.GetType().Name}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        coordinator.Changed -= OnCoordinatorChanged;
        coordinator.UpdateAvailable -= OnUpdateAvailable;
        downloader.Dispose();
        downloadGate.Dispose();
        await coordinator.DisposeAsync().ConfigureAwait(false);
    }
}
