using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexQuotaTray.Core.Presentation;

public interface ISettingsPlatformActions
{
    bool CanConfigureStartup { get; }

    Task SetStartupAsync(bool enabled, CancellationToken cancellationToken);

    void OpenDataDirectory();

    Task<int> ImportProductionDataAsync(CancellationToken cancellationToken);

    Task ClearQuotaCacheAsync();
}

public interface ISettingsPageActions
{
    Task RefreshQuotaAsync(CancellationToken cancellationToken);

    void OpenOfficialUsage();

    void CopyQuotaSummary();

    void CopyDiagnostics();

    void ShowAbout(object? host);
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IQuotaRuntimeControl runtime;
    private readonly ISettingsPlatformActions platform;
    private readonly ISettingsPageActions pageActions;
    private readonly SemaphoreSlim applyGate = new(1, 1);
    private bool suppressSettingsApply = true;
    private long settingsRevision;

    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool showRemainingPercent;
    [ObservableProperty] private bool use24HourTime;
    [ObservableProperty] private bool persistQuotaCache;
    [ObservableProperty] private bool refreshOnNetworkRestore;
    [ObservableProperty] private bool notifyRemaining50;
    [ObservableProperty] private bool notifyRemaining20;
    [ObservableProperty] private bool notifyRemaining10;
    [ObservableProperty] private bool notifyAfterQuotaReset;
    [ObservableProperty] private bool silentStartup;
    [ObservableProperty] private RefreshMode selectedRefreshMode;
    [ObservableProperty] private ThemeMode selectedThemeMode;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private bool isBusy;

    public SettingsViewModel(
        IQuotaRuntimeControl runtime,
        ISettingsPlatformActions platform,
        ISettingsPageActions pageActions)
    {
        this.runtime = runtime;
        this.platform = platform;
        this.pageActions = pageActions;
        Load(runtime.Settings);
        suppressSettingsApply = false;
    }

    public event EventHandler<ThemeMode>? ThemeSaved;

    public bool CanConfigureStartup => platform.CanConfigureStartup;

    public string StartupDescription => CanConfigureStartup
        ? "登录 Windows 时自动启动托盘应用。"
        : "预览模式不可配置开机启动。";

    public IReadOnlyList<RefreshMode> RefreshModes { get; } = Enum.GetValues<RefreshMode>();

    public IReadOnlyList<ThemeMode> ThemeModes { get; } = Enum.GetValues<ThemeMode>();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task ResetAsync(CancellationToken cancellationToken)
    {
        InvalidatePendingApply();
        Load(AppSettings.Defaults);
        return ApplyCurrentSettingsAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task ClearQuotaCacheAsync()
    {
        await platform.ClearQuotaCacheAsync();
        StatusText = "额度缓存已清除；提醒防重复状态已保留";
    }

    [RelayCommand]
    private async Task ImportProductionDataAsync(CancellationToken cancellationToken)
    {
        var count = await platform.ImportProductionDataAsync(cancellationToken);
        StatusText = count == 0 ? "没有可导入的有效正式版数据" : $"已导入 {count} 个数据文件，重启后生效";
    }

    [RelayCommand]
    private void OpenDataDirectory() => platform.OpenDataDirectory();

    [RelayCommand]
    private Task RefreshQuotaAsync(CancellationToken cancellationToken) =>
        pageActions.RefreshQuotaAsync(cancellationToken);

    [RelayCommand]
    private void OpenOfficialUsage() => pageActions.OpenOfficialUsage();

    [RelayCommand]
    private void CopyQuotaSummary() => pageActions.CopyQuotaSummary();

    [RelayCommand]
    private void CopyDiagnostics() => pageActions.CopyDiagnostics();

    [RelayCommand]
    private void ShowAbout(object? host) => pageActions.ShowAbout(host);

    private bool CanEdit() => !IsBusy;

    private Task ApplyCurrentSettingsAsync(CancellationToken cancellationToken) =>
        ApplySettingsAsync(ToSettings(), cancellationToken);

    private void QueueSettingsApply()
    {
        if (suppressSettingsApply)
        {
            return;
        }

        var revision = Interlocked.Increment(ref settingsRevision);
        _ = ApplyLatestSettingsAsync(revision);
    }

    private async Task ApplyLatestSettingsAsync(long revision)
    {
        await applyGate.WaitAsync();
        try
        {
            if (revision != Interlocked.Read(ref settingsRevision))
            {
                return;
            }

            await ApplySettingsCoreAsync(ToSettings(), CancellationToken.None);
        }
        finally
        {
            applyGate.Release();
        }
    }

    private async Task ApplySettingsAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        await applyGate.WaitAsync(cancellationToken);
        try
        {
            await ApplySettingsCoreAsync(settings, cancellationToken);
        }
        finally
        {
            applyGate.Release();
        }
    }

    private async Task ApplySettingsCoreAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var previous = Normalize(runtime.Settings);
        IsBusy = true;
        try
        {
            if (CanConfigureStartup && previous.StartWithWindows != settings.StartWithWindows)
            {
                await platform.SetStartupAsync(settings.StartWithWindows, cancellationToken);
            }

            await runtime.ApplySettingsAsync(settings, cancellationToken);
            if (previous.ThemeMode != settings.ThemeMode)
            {
                ThemeSaved?.Invoke(this, settings.ThemeMode);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (CanConfigureStartup && previous.StartWithWindows != settings.StartWithWindows)
            {
                try
                {
                    await platform.SetStartupAsync(previous.StartWithWindows, CancellationToken.None);
                }
                catch (Exception rollbackError) when (
                    rollbackError is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // Preserve the original failure while leaving the best-effort rollback contained.
                }
            }

            Load(previous);
            StatusText = "应用设置失败，已恢复原设置";
            if (previous.ThemeMode != settings.ThemeMode)
            {
                ThemeSaved?.Invoke(this, previous.ThemeMode);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void InvalidatePendingApply() => Interlocked.Increment(ref settingsRevision);

    private AppSettings ToSettings() => new(
        CanConfigureStartup && StartWithWindows,
        ShowRemainingPercent,
        Use24HourTime,
        PersistQuotaCache,
        SelectedRefreshMode,
        RefreshOnNetworkRestore,
        new NotificationSettings(NotifyRemaining50, NotifyRemaining20, NotifyRemaining10, NotifyAfterQuotaReset),
        SelectedThemeMode,
        SilentStartup);

    private AppSettings Normalize(AppSettings value) => value with
    {
        StartWithWindows = CanConfigureStartup && value.StartWithWindows,
        Notifications = value.EffectiveNotifications,
    };

    private void Load(AppSettings value)
    {
        suppressSettingsApply = true;
        try
        {
            StartWithWindows = CanConfigureStartup && value.StartWithWindows;
            ShowRemainingPercent = value.ShowRemainingPercent;
            Use24HourTime = value.Use24HourTime;
            PersistQuotaCache = value.PersistQuotaCache;
            RefreshOnNetworkRestore = value.RefreshOnNetworkRestore;
            NotifyRemaining50 = value.EffectiveNotifications.Remaining50;
            NotifyRemaining20 = value.EffectiveNotifications.Remaining20;
            NotifyRemaining10 = value.EffectiveNotifications.Remaining10;
            NotifyAfterQuotaReset = value.EffectiveNotifications.ResetAfterCycle;
            SelectedRefreshMode = value.RefreshMode;
            SelectedThemeMode = value.ThemeMode;
            SilentStartup = value.SilentStartup;
        }
        finally
        {
            suppressSettingsApply = false;
        }
    }

    partial void OnStartWithWindowsChanged(bool value) => QueueSettingsApply();

    partial void OnShowRemainingPercentChanged(bool value) => QueueSettingsApply();

    partial void OnUse24HourTimeChanged(bool value) => QueueSettingsApply();

    partial void OnPersistQuotaCacheChanged(bool value) => QueueSettingsApply();

    partial void OnRefreshOnNetworkRestoreChanged(bool value) => QueueSettingsApply();

    partial void OnNotifyRemaining50Changed(bool value) => QueueSettingsApply();

    partial void OnNotifyRemaining20Changed(bool value) => QueueSettingsApply();

    partial void OnNotifyRemaining10Changed(bool value) => QueueSettingsApply();

    partial void OnNotifyAfterQuotaResetChanged(bool value) => QueueSettingsApply();

    partial void OnSilentStartupChanged(bool value) => QueueSettingsApply();

    partial void OnSelectedRefreshModeChanged(RefreshMode value) => QueueSettingsApply();

    partial void OnSelectedThemeModeChanged(ThemeMode value) => QueueSettingsApply();
}
