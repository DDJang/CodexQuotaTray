using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Runtime;
using CodexQuotaTray.Core.Updates;
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

    string TokenSyncStatusText { get; }

    string TokenSyncAddressText { get; }

    string TokenSyncDeviceNameText { get; }

    string? TokenSyncPairingInfo { get; }

    event EventHandler? TokenSyncChanged;

    Task ApplyTokenSyncEnabledAsync(bool enabled, CancellationToken cancellationToken);

    void CopyTokenSyncPairingInfo();

    Task RegenerateTokenSyncSecretAsync(CancellationToken cancellationToken);
}

public interface ISettingsPageActions
{
    Task RefreshQuotaAsync(CancellationToken cancellationToken);

    void OpenOfficialUsage();

    void CopyQuotaSummary();

    void CopyDiagnostics();

    void ShowAbout(object? host);
}

public sealed record PercentageDisplayModeOption(bool ShowRemainingPercent, string DisplayName);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IQuotaRuntimeControl runtime;
    private readonly ISettingsPlatformActions platform;
    private readonly ISettingsPageActions pageActions;
    private readonly IWindowsUpdateController? updates;
    private readonly SemaphoreSlim applyGate = new(1, 1);
    private bool suppressSettingsApply = true;
    private bool suppressUpdateApply = true;
    private long settingsRevision;

    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool showRemainingPercent;
    [ObservableProperty] private bool use24HourTime;
    [ObservableProperty] private bool persistQuotaCache;
    [ObservableProperty] private bool refreshOnPanelOpen;
    [ObservableProperty] private bool refreshOnNetworkRestore;
    [ObservableProperty] private bool notifyRemaining50;
    [ObservableProperty] private bool notifyRemaining20;
    [ObservableProperty] private bool notifyRemaining10;
    [ObservableProperty] private bool notifyAfterQuotaReset;
    [ObservableProperty] private bool silentStartup;
    [ObservableProperty] private bool phoneTokenSyncEnabled;
    [ObservableProperty] private string tokenSyncStatusText = string.Empty;
    [ObservableProperty] private string tokenSyncAddressText = string.Empty;
    [ObservableProperty] private string tokenSyncDeviceNameText = string.Empty;
    [ObservableProperty] private string? tokenSyncPairingInfo;
    [ObservableProperty] private RefreshMode selectedRefreshMode;
    [ObservableProperty] private ThemeMode selectedThemeMode;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private bool automaticUpdateChecksEnabled;
    [ObservableProperty] private bool updateRemindersEnabled;
    [ObservableProperty] private string updateStatusText = "尚未检查";
    [ObservableProperty] private string updateLastCheckText = "尚未检查";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForWindowsUpdates))]
    private bool updateCheckInProgress;
    [ObservableProperty] private bool updateDownloadInProgress;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private bool isBusy;

    public SettingsViewModel(
        IQuotaRuntimeControl runtime,
        ISettingsPlatformActions platform,
        ISettingsPageActions pageActions,
        IWindowsUpdateController? updates = null)
    {
        this.runtime = runtime;
        this.platform = platform;
        this.pageActions = pageActions;
        this.updates = updates;
        platform.TokenSyncChanged += OnTokenSyncChanged;
        if (updates is not null)
        {
            updates.Changed += OnWindowsUpdateChanged;
        }
        Load(runtime.Settings);
        RefreshTokenSyncStatus();
        RefreshWindowsUpdateStatus();
        suppressUpdateApply = false;
        suppressSettingsApply = false;
    }

    public event EventHandler<ThemeMode>? ThemeSaved;

    public event EventHandler? TokenSyncChanged;

    public event EventHandler<WindowsUpdateCheckResult>? UpdateCheckCompleted;

    public bool IsWindowsUpdateAvailable => updates?.IsProduction == true;

    public string WindowsUpdateAvailabilityText => IsWindowsUpdateAvailable
        ? string.Empty
        : "开发版本不检查正式更新";

    public string CurrentVersionText => ProductVersion.Current;

    public bool CanDownloadWindowsUpdate => updates?.CurrentResult.HasUpdate == true && !UpdateDownloadInProgress;

    public bool CanEditUpdateReminders => IsWindowsUpdateAvailable && AutomaticUpdateChecksEnabled;

    public bool CanCheckForWindowsUpdates => IsWindowsUpdateAvailable && !UpdateCheckInProgress;

    public bool CanConfigureStartup => platform.CanConfigureStartup;

    public string StartupDescription => CanConfigureStartup
        ? "登录 Windows 时自动启动托盘应用。"
        : "预览模式不可配置开机启动。";

    public IReadOnlyList<RefreshMode> RefreshModes { get; } =
    [
        RefreshMode.Every5Minutes,
        RefreshMode.Every15Minutes,
        RefreshMode.Every30Minutes,
        RefreshMode.ManualOnly,
    ];

    public IReadOnlyList<ThemeMode> ThemeModes { get; } = Enum.GetValues<ThemeMode>();

    public IReadOnlyList<PercentageDisplayModeOption> PercentageDisplayModes { get; } =
    [
        new(true, "剩余百分比"),
        new(false, "使用百分比"),
    ];

    public PercentageDisplayModeOption SelectedPercentageDisplayMode
    {
        get => PercentageDisplayModes.First(option => option.ShowRemainingPercent == ShowRemainingPercent);
        set
        {
            if (value is not null)
            {
                ShowRemainingPercent = value.ShowRemainingPercent;
            }
        }
    }

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

    [RelayCommand]
    private async Task CheckForWindowsUpdatesAsync(CancellationToken cancellationToken)
    {
        if (updates is null || !updates.IsProduction || UpdateCheckInProgress)
        {
            return;
        }

        UpdateCheckInProgress = true;
        try
        {
            var result = await updates.CheckAsync(manual: true, cancellationToken);
            RefreshWindowsUpdateStatus();
            UpdateCheckCompleted?.Invoke(this, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            UpdateStatusText = "检查更新失败";
            UpdateCheckCompleted?.Invoke(
                this,
                new WindowsUpdateCheckResult(
                    WindowsUpdateCheckStatus.Failed,
                    null,
                    "检查更新失败",
                    DateTimeOffset.UtcNow));
        }
        finally
        {
            UpdateCheckInProgress = false;
            RefreshWindowsUpdateStatus();
        }
    }

    public async Task<WindowsUpdateDownloadResult> DownloadWindowsUpdateAsync(CancellationToken cancellationToken)
    {
        if (updates is null || !updates.IsProduction || UpdateDownloadInProgress)
        {
            return WindowsUpdateDownloadResult.Failed("开发版本不检查正式更新");
        }

        UpdateDownloadInProgress = true;
        try
        {
            var result = await updates.DownloadAsync(cancellationToken);
            StatusText = result.Succeeded ? "更新已准备好" : result.ErrorMessage ?? "更新下载失败";
            return result;
        }
        finally
        {
            UpdateDownloadInProgress = false;
            OnPropertyChanged(nameof(CanDownloadWindowsUpdate));
        }
    }

    public Task<bool> InstallPreparedWindowsUpdateAsync(CancellationToken cancellationToken) =>
        updates?.InstallPreparedAsync(cancellationToken) ?? Task.FromResult(false);

    [RelayCommand]
    private void CopyTokenSyncPairingInfo()
    {
        try
        {
            platform.CopyTokenSyncPairingInfo();
            StatusText = "配对信息已复制";
        }
        catch (InvalidOperationException)
        {
            RefreshTokenSyncStatus();
            StatusText = "同步服务当前未监听，无法复制配对信息";
        }
    }

    [RelayCommand]
    private async Task RegenerateTokenSyncSecretAsync(CancellationToken cancellationToken)
    {
        await platform.RegenerateTokenSyncSecretAsync(cancellationToken);
        RefreshTokenSyncStatus();
        StatusText = "配对密钥已重新生成；旧配对已失效";
    }

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
            if (previous.PhoneTokenSyncEnabled != settings.PhoneTokenSyncEnabled)
            {
                await platform.ApplyTokenSyncEnabledAsync(settings.PhoneTokenSyncEnabled, cancellationToken);
                RefreshTokenSyncStatus();
            }
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

    private AppSettings ToSettings() => SettingsService.Normalize(new AppSettings(
        StartWithWindows: CanConfigureStartup && StartWithWindows,
        ShowRemainingPercent: ShowRemainingPercent,
        Use24HourTime: Use24HourTime,
        PersistQuotaCache: PersistQuotaCache,
        RefreshMode: SelectedRefreshMode,
        RefreshOnPanelOpen: RefreshOnPanelOpen,
        RefreshOnNetworkRestore: RefreshOnNetworkRestore,
        Notifications: new NotificationSettings(NotifyRemaining50, NotifyRemaining20, NotifyRemaining10, NotifyAfterQuotaReset),
        ThemeMode: SelectedThemeMode,
        SilentStartup: SilentStartup,
        PhoneTokenSyncEnabled: PhoneTokenSyncEnabled));

    private AppSettings Normalize(AppSettings value) => SettingsService.Normalize(value) with
    {
        StartWithWindows = CanConfigureStartup && value.StartWithWindows,
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
            RefreshOnPanelOpen = value.RefreshOnPanelOpen;
            RefreshOnNetworkRestore = value.RefreshOnNetworkRestore;
            NotifyRemaining50 = value.EffectiveNotifications.Remaining50;
            NotifyRemaining20 = value.EffectiveNotifications.Remaining20;
            NotifyRemaining10 = value.EffectiveNotifications.Remaining10;
            NotifyAfterQuotaReset = value.EffectiveNotifications.ResetAfterCycle;
            SelectedRefreshMode = value.RefreshMode == RefreshMode.Auto
                ? RefreshMode.Every15Minutes
                : value.RefreshMode;
            SelectedThemeMode = value.ThemeMode;
            SilentStartup = value.SilentStartup;
            PhoneTokenSyncEnabled = value.PhoneTokenSyncEnabled;
        }
        finally
        {
            suppressSettingsApply = false;
        }
    }

    partial void OnStartWithWindowsChanged(bool value) => QueueSettingsApply();

    partial void OnShowRemainingPercentChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedPercentageDisplayMode));
        QueueSettingsApply();
    }

    partial void OnUse24HourTimeChanged(bool value) => QueueSettingsApply();

    partial void OnPersistQuotaCacheChanged(bool value) => QueueSettingsApply();

    partial void OnRefreshOnPanelOpenChanged(bool value) => QueueSettingsApply();

    partial void OnRefreshOnNetworkRestoreChanged(bool value) => QueueSettingsApply();

    partial void OnNotifyRemaining50Changed(bool value) => QueueSettingsApply();

    partial void OnNotifyRemaining20Changed(bool value) => QueueSettingsApply();

    partial void OnNotifyRemaining10Changed(bool value) => QueueSettingsApply();

    partial void OnNotifyAfterQuotaResetChanged(bool value) => QueueSettingsApply();

    partial void OnSilentStartupChanged(bool value) => QueueSettingsApply();

    partial void OnPhoneTokenSyncEnabledChanged(bool value) => QueueSettingsApply();

    partial void OnSelectedRefreshModeChanged(RefreshMode value) => QueueSettingsApply();

    partial void OnSelectedThemeModeChanged(ThemeMode value) => QueueSettingsApply();

    partial void OnAutomaticUpdateChecksEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditUpdateReminders));
        if (!suppressUpdateApply && updates is not null)
        {
            _ = ApplyAutomaticUpdateSettingAsync(value);
        }
    }

    partial void OnUpdateRemindersEnabledChanged(bool value)
    {
        if (!suppressUpdateApply && updates is not null)
        {
            _ = ApplyUpdateReminderSettingAsync(value);
        }
    }

    public void RefreshTokenSyncStatus()
    {
        TokenSyncStatusText = platform.TokenSyncStatusText;
        TokenSyncDeviceNameText = string.IsNullOrWhiteSpace(platform.TokenSyncDeviceNameText)
            ? string.Empty
            : $"电脑：{platform.TokenSyncDeviceNameText}";
        TokenSyncAddressText = string.IsNullOrWhiteSpace(platform.TokenSyncAddressText)
            ? string.Empty
            : $"Windows 地址：{platform.TokenSyncAddressText}";
        TokenSyncPairingInfo = platform.TokenSyncPairingInfo;
    }

    public void RefreshWindowsUpdateStatus()
    {
        suppressUpdateApply = true;
        try
        {
            if (updates is null || !updates.IsProduction)
            {
                AutomaticUpdateChecksEnabled = false;
                UpdateRemindersEnabled = false;
                UpdateStatusText = "开发版本不检查正式更新";
                UpdateLastCheckText = "不适用于开发版本";
                return;
            }

            AutomaticUpdateChecksEnabled = updates.AutomaticChecksEnabled;
            UpdateRemindersEnabled = updates.UpdateRemindersEnabled;
            OnPropertyChanged(nameof(CanEditUpdateReminders));
            var result = updates.CurrentResult;
            UpdateStatusText = FormatUpdateStatus(result);
            var lastCheck = updates.LastSuccessfulCheckUtc ?? updates.LastAttemptUtc;
            UpdateLastCheckText = lastCheck is { } value
                ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "尚未检查";
            OnPropertyChanged(nameof(CanDownloadWindowsUpdate));
        }
        finally
        {
            suppressUpdateApply = false;
        }
    }

    private async Task ApplyAutomaticUpdateSettingAsync(bool value)
    {
        try
        {
            await updates!.SetAutomaticChecksEnabledAsync(value, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RefreshWindowsUpdateStatus();
            StatusText = "更新设置保存失败";
        }
    }

    private async Task ApplyUpdateReminderSettingAsync(bool value)
    {
        try
        {
            await updates!.SetUpdateRemindersEnabledAsync(value, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RefreshWindowsUpdateStatus();
            StatusText = "更新设置保存失败";
        }
    }

    private static string FormatUpdateStatus(WindowsUpdateCheckResult result) => result.Status switch
    {
        WindowsUpdateCheckStatus.NotChecked => "尚未检查",
        WindowsUpdateCheckStatus.Checking => "正在检查…",
        WindowsUpdateCheckStatus.Disabled => "自动检查已关闭",
        WindowsUpdateCheckStatus.Skipped => "24 小时内已检查过",
        WindowsUpdateCheckStatus.UpToDate => $"已是最新版本 {result.Release?.Version.ToString() ?? ProductVersion.Current}",
        WindowsUpdateCheckStatus.Available => $"发现新版本 {result.Release?.Version}",
        WindowsUpdateCheckStatus.NoRelease => "当前 Release 没有有效的 Windows 安装包",
        WindowsUpdateCheckStatus.Failed => "检查更新失败",
        _ => "尚未检查",
    };

    private void OnWindowsUpdateChanged(object? sender, EventArgs args)
    {
        RefreshWindowsUpdateStatus();
    }

    private void OnTokenSyncChanged(object? sender, EventArgs args) => TokenSyncChanged?.Invoke(this, args);
}
