using CodexQuotaTray.Core.Auth;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Protocol;
using CodexQuotaTray.Core.Runtime;
using CodexQuotaTray.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexQuotaTray.Core.Presentation;

public sealed class DataSourcesChangedEventArgs : EventArgs
{
    public DataSourcesChangedEventArgs(bool quotaDataSourceChanged, bool tokenUsageDataSourceChanged)
    {
        QuotaDataSourceChanged = quotaDataSourceChanged;
        TokenUsageDataSourceChanged = tokenUsageDataSourceChanged;
    }

    public bool QuotaDataSourceChanged { get; }

    public bool TokenUsageDataSourceChanged { get; }
}

public interface ISettingsPlatformActions
{
    bool CanConfigureStartup { get; }

    Task SetStartupAsync(bool enabled, CancellationToken cancellationToken);

    void OpenDataDirectory();

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

    Task OpenWindowsUpdateBrowserAsync(CancellationToken cancellationToken);

    void CopyDiagnostics();
}

public sealed record PercentageDisplayModeOption(bool ShowRemainingPercent, string DisplayName);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IQuotaRuntimeControl runtime;
    private readonly ISettingsPlatformActions platform;
    private readonly ISettingsPageActions pageActions;
    private readonly IWindowsUpdateController? updates;
    private readonly WindowsAccountService? account;
    private readonly SemaphoreSlim applyGate = new(1, 1);
    private readonly SemaphoreSlim dataSourceGate = new(1, 1);
    private CancellationTokenSource? oauthLoginCancellationSource;
    private bool suppressSettingsApply = true;
    private bool suppressUpdateApply = true;
    private long settingsRevision;
    private CancellationTokenSource? downloadCancellationSource;
    private Task<WindowsUpdateDownloadResult>? downloadTask;

    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool showRemainingPercent;
    [ObservableProperty] private bool persistQuotaCache;
    [ObservableProperty] private bool persistTokenUsageCache;
    [ObservableProperty] private bool refreshOnPanelOpen;
    [ObservableProperty] private bool refreshOnNetworkRestore;
    [ObservableProperty] private bool notifyRemaining50;
    [ObservableProperty] private bool notifyRemaining20;
    [ObservableProperty] private bool notifyRemaining10;
    [ObservableProperty] private bool notifyAfterQuotaReset;
    [ObservableProperty] private bool notifyResetCreditExpiry;
    [ObservableProperty] private int resetCreditExpiryLeadHours = 24;
    [ObservableProperty] private bool silentStartup;
    [ObservableProperty] private bool phoneTokenSyncEnabled;
    [ObservableProperty] private bool tokenRefreshOnPanelOpen;
    [ObservableProperty] private string tokenSyncStatusText = string.Empty;
    [ObservableProperty] private string tokenSyncAddressText = string.Empty;
    [ObservableProperty] private string tokenSyncDeviceNameText = string.Empty;
    [ObservableProperty] private string? tokenSyncPairingInfo;
    [ObservableProperty] private RefreshMode selectedRefreshMode;
    [ObservableProperty] private RefreshMode selectedTokenRefreshMode;
    [ObservableProperty] private ThemeMode selectedThemeMode;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedQuotaDataSourceDescription))]
    [NotifyPropertyChangedFor(nameof(SelectedQuotaDataSourceIndex))]
    private QuotaDataSource selectedQuotaDataSource;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTokenUsageDataSourceDescription))]
    [NotifyPropertyChangedFor(nameof(SelectedTokenUsageDataSourceIndex))]
    private TokenUsageDataSource selectedTokenUsageDataSource;
    [ObservableProperty] private string cliAccountStatusText = "尚未检查";
    [ObservableProperty] private bool cliAccountAvailable;
    private string oauthAccountStatusText = "尚未检查";
    private string oauthUserCodeText = string.Empty;
    private string oauthVerificationUrl = string.Empty;
    private bool oauthLoginInProgress;
    private bool oauthAvailable;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditDataSources))]
    private bool dataSourceChangeInProgress;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private bool automaticUpdateChecksEnabled;
    [ObservableProperty] private bool updateRemindersEnabled;
    [ObservableProperty] private bool autoLaunchInstallerAfterDownload;
    [NotifyPropertyChangedFor(nameof(HasUpdateStatusText))]
    [ObservableProperty] private string updateStatusText = "尚未检查";
    [ObservableProperty] private string updateLastCheckText = "尚未检查";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForWindowsUpdates))]
    private bool updateCheckInProgress;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelWindowsUpdateCommand))]
    [NotifyPropertyChangedFor(nameof(CanDownloadWindowsUpdate))]
    private bool updateDownloadInProgress;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownloadProgress))]
    [NotifyPropertyChangedFor(nameof(IsDownloadProgressIndeterminate))]
    [NotifyPropertyChangedFor(nameof(DownloadProgressValue))]
    [NotifyPropertyChangedFor(nameof(DownloadProgressPercentageText))]
    [NotifyPropertyChangedFor(nameof(DownloadProgressText))]
    [NotifyPropertyChangedFor(nameof(DownloadProgressSizeText))]
    [NotifyPropertyChangedFor(nameof(CanDownloadWindowsUpdate))]
    private WindowsUpdateDownloadProgress downloadProgress = WindowsUpdateDownloadProgress.Idle;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditDataSources))]
    private bool isBusy;

    public string OAuthAccountStatusText
    {
        get => oauthAccountStatusText;
        private set => SetProperty(ref oauthAccountStatusText, value);
    }

    public string OAuthUserCodeText
    {
        get => oauthUserCodeText;
        private set
        {
            if (SetProperty(ref oauthUserCodeText, value))
            {
                OnPropertyChanged(nameof(OAuthUserCodeDisplayText));
                OnPropertyChanged(nameof(OAuthUserCodeClipboardText));
            }
        }
    }

    public string OAuthVerificationUrl
    {
        get => oauthVerificationUrl;
        private set
        {
            if (SetProperty(ref oauthVerificationUrl, value))
            {
                OnPropertyChanged(nameof(ShowOAuthDeviceLoginDetails));
                OnPropertyChanged(nameof(ShowOAuthLoginPreparing));
                OnPropertyChanged(nameof(OAuthVerificationDisplayText));
            }
        }
    }

    public bool OAuthLoginInProgress
    {
        get => oauthLoginInProgress;
        private set
        {
            if (SetProperty(ref oauthLoginInProgress, value))
            {
                OnPropertyChanged(nameof(ShowOAuthLoginButton));
                OnPropertyChanged(nameof(ShowOAuthCancelButton));
                OnPropertyChanged(nameof(ShowOAuthDeviceLoginDetails));
                OnPropertyChanged(nameof(ShowOAuthLoginPreparing));
            }
        }
    }

    public bool OAuthAvailable
    {
        get => oauthAvailable;
        private set
        {
            if (SetProperty(ref oauthAvailable, value))
            {
                OnPropertyChanged(nameof(IsOAuthAvailable));
                OnPropertyChanged(nameof(ShowOAuthLoginButton));
                OnPropertyChanged(nameof(ShowOAuthCancelButton));
                OnPropertyChanged(nameof(ShowOAuthLogoutButton));
            }
        }
    }

    public SettingsViewModel(
        IQuotaRuntimeControl runtime,
        ISettingsPlatformActions platform,
        ISettingsPageActions pageActions,
        IWindowsUpdateController? updates = null,
        WindowsAccountService? account = null)
    {
        this.runtime = runtime;
        this.platform = platform;
        this.pageActions = pageActions;
        this.updates = updates;
        this.account = account;
        platform.TokenSyncChanged += OnTokenSyncChanged;
        if (updates is not null)
        {
            updates.Changed += OnWindowsUpdateChanged;
            updates.DownloadProgressChanged += OnWindowsUpdateProgressChanged;
        }
        Load(runtime.Settings);
        RefreshTokenSyncStatus();
        RefreshAccountStatusPresentation(null);
        RefreshWindowsUpdateStatus();
        suppressUpdateApply = false;
        suppressSettingsApply = false;
    }

    public event EventHandler<ThemeMode>? ThemeSaved;

    public event EventHandler? TokenSyncChanged;

    public event EventHandler<DataSourcesChangedEventArgs>? DataSourcesChanged;

    public event EventHandler<WindowsUpdateCheckResult>? UpdateCheckCompleted;

    public bool IsWindowsUpdateAvailable => updates?.IsProduction == true;

    public string CurrentVersionText => ProductVersion.Current;

    public bool CanDownloadWindowsUpdate => updates?.CurrentResult.HasUpdate == true
        && !UpdateDownloadInProgress
        && DownloadProgress.Phase is
            WindowsUpdateDownloadPhase.Idle or
            WindowsUpdateDownloadPhase.Cancelled or
            WindowsUpdateDownloadPhase.Failed;

    public bool CanEditUpdateReminders => IsWindowsUpdateAvailable && AutomaticUpdateChecksEnabled;

    public bool CanCheckForWindowsUpdates => !UpdateCheckInProgress;

    public bool HasUpdateStatusText => !string.IsNullOrEmpty(UpdateStatusText);

    public bool HasDownloadProgress => DownloadProgress.Phase is
        WindowsUpdateDownloadPhase.Downloading or WindowsUpdateDownloadPhase.Verifying;

    public bool IsDownloadProgressIndeterminate => DownloadProgress.Phase == WindowsUpdateDownloadPhase.Downloading
        && DownloadProgress.Percentage is null;

    public double DownloadProgressValue => DownloadProgress.Percentage
        ?? (DownloadProgress.Phase == WindowsUpdateDownloadPhase.Verifying ? 100 : 0);

    public string DownloadProgressPercentageText => DownloadProgress switch
    {
        {
            Phase: WindowsUpdateDownloadPhase.Downloading or WindowsUpdateDownloadPhase.Verifying,
            Percentage: { } percentage,
        } => $"{percentage}%",
        _ => string.Empty,
    };

    public string DownloadProgressText => DownloadProgress.Phase switch
    {
        WindowsUpdateDownloadPhase.Downloading when updates?.CurrentResult.Release is { } release => $"正在下载 {release.Version}",
        WindowsUpdateDownloadPhase.Downloading => "正在下载更新…",
        WindowsUpdateDownloadPhase.Verifying => "正在校验安装包…",
        WindowsUpdateDownloadPhase.ReadyToInstall => "更新已准备好",
        WindowsUpdateDownloadPhase.Cancelled => "下载已取消",
        WindowsUpdateDownloadPhase.Failed => "更新下载失败",
        WindowsUpdateDownloadPhase.Installing => "正在启动安装程序…",
        _ => string.Empty,
    };

    public string DownloadProgressSizeText
    {
        get
        {
            if (DownloadProgress.Phase is not (WindowsUpdateDownloadPhase.Downloading or WindowsUpdateDownloadPhase.Verifying))
            {
                return string.Empty;
            }

            var text = WindowsUpdateDownloadFormatting.FormatSize(
                DownloadProgress.BytesDownloaded,
                DownloadProgress.TotalBytes);
            return DownloadProgress.Phase == WindowsUpdateDownloadPhase.Downloading
                && DownloadProgress.BytesPerSecond is > 0
                ? $"{text} · {WindowsUpdateDownloadFormatting.FormatSpeed(DownloadProgress.BytesPerSecond.Value)}"
                : text;
        }
    }

    public bool CanConfigureStartup => platform.CanConfigureStartup;

    public bool IsOAuthAvailable => OAuthAvailable;

    public bool CanEditDataSources => !IsBusy && !DataSourceChangeInProgress;

    public bool ShowOAuthLoginButton => !OAuthAvailable && !OAuthLoginInProgress;

    public bool ShowOAuthCancelButton => !OAuthAvailable && OAuthLoginInProgress;

    public bool ShowOAuthLogoutButton => OAuthAvailable;

    public bool ShowOAuthDeviceLoginDetails =>
        OAuthLoginInProgress && !string.IsNullOrWhiteSpace(OAuthVerificationUrl);

    public bool ShowOAuthLoginPreparing =>
        OAuthLoginInProgress && string.IsNullOrWhiteSpace(OAuthVerificationUrl);

    public string OAuthUserCodeDisplayText => $"代码：{OAuthUserCodeText}";

    public string OAuthUserCodeClipboardText =>
        OAuthUserCodeText.Replace("-", string.Empty, StringComparison.Ordinal);

    public string OAuthVerificationDisplayText => $"验证网址：{OAuthVerificationUrl}";

    public int SelectedQuotaDataSourceIndex => (int)SelectedQuotaDataSource;

    public int SelectedTokenUsageDataSourceIndex => (int)SelectedTokenUsageDataSource;

    public string SelectedQuotaDataSourceDescription => SelectedQuotaDataSource switch
    {
        QuotaDataSource.CodexCli => "通过本机 Codex CLI 的 App Server 获取账户额度，需要 Codex CLI 已登录。",
        QuotaDataSource.OAuth => "使用 CodexQuotaTray 独立 OAuth 登录直接读取 OpenAI 账户额度，不依赖 Codex CLI。",
        _ => "请选择额度来源。",
    };

    public string SelectedTokenUsageDataSourceDescription => SelectedTokenUsageDataSource switch
    {
        TokenUsageDataSource.Local => "仅统计当前电脑可读取的 Codex session；依赖本地 session 文件，删除后历史无法恢复。",
        TokenUsageDataSource.CodexCli => "统计 OpenAI/Codex 账户活动，可能包含其他设备或 Remote/Cloud；服务端每日数据可能有延迟，需要 Codex CLI 已登录。",
        TokenUsageDataSource.OAuth => "统计 OpenAI/Codex 账户活动，可能包含其他设备或 Remote/Cloud；使用独立 OAuth 登录，服务端每日数据可能有延迟。",
        _ => "请选择统计来源。",
    };

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

    public int SelectedResetCreditExpiryLeadIndex
    {
        get => ResetCreditExpiryLeadHours switch
        {
            6 => 1,
            1 => 2,
            _ => 0,
        };
        set
        {
            var hours = value switch
            {
                1 => 6,
                2 => 1,
                _ => 24,
            };
            if (ResetCreditExpiryLeadHours != hours)
            {
                ResetCreditExpiryLeadHours = hours;
            }
        }
    }

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
        Load(AppSettings.Defaults with
        {
            QuotaDataSource = runtime.Settings.QuotaDataSource,
            TokenUsageDataSource = runtime.Settings.TokenUsageDataSource,
        });
        return ApplyCurrentSettingsAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task ClearQuotaCacheAsync()
    {
        await platform.ClearQuotaCacheAsync();
        StatusText = "额度缓存已清除；提醒防重复状态已保留";
    }

    [RelayCommand]
    private void OpenDataDirectory() => platform.OpenDataDirectory();

    [RelayCommand]
    private Task RefreshQuotaAsync(CancellationToken cancellationToken) =>
        pageActions.RefreshQuotaAsync(cancellationToken);

    public Task SelectQuotaDataSourceAsync(QuotaDataSource source, CancellationToken cancellationToken) =>
        SelectDataSourceAsync(source, null, cancellationToken);

    public Task SelectStatisticsDataSourceAsync(TokenUsageDataSource source, CancellationToken cancellationToken) =>
        SelectDataSourceAsync(null, source, cancellationToken);

    private async Task SelectDataSourceAsync(
        QuotaDataSource? quotaSource,
        TokenUsageDataSource? statisticsSource,
        CancellationToken cancellationToken)
    {
        if (quotaSource is not null && !Enum.IsDefined(quotaSource.Value)
            || statisticsSource is not null && !Enum.IsDefined(statisticsSource.Value))
        {
            return;
        }

        await dataSourceGate.WaitAsync(cancellationToken);
        DataSourceChangeInProgress = true;
        try
        {
            var current = runtime.Settings;
            var requestedQuota = quotaSource ?? current.QuotaDataSource;
            var requestedStatistics = statisticsSource ?? current.TokenUsageDataSource;
            if (requestedQuota == current.QuotaDataSource
                && requestedStatistics == current.TokenUsageDataSource)
            {
                RestoreDataSourceSelections();
                return;
            }

            if (quotaSource == QuotaDataSource.OAuth
                || statisticsSource == TokenUsageDataSource.OAuth)
            {
                if (account is null
                    || !await HasUsableOAuthCredentialsAsync(cancellationToken))
                {
                    StatusText = "请先登录可用的 OAuth 账户";
                    RestoreDataSourceSelections();
                    return;
                }
            }

            if (quotaSource == QuotaDataSource.CodexCli
                || statisticsSource == TokenUsageDataSource.CodexCli)
            {
                if (account is null
                    || !await account.HasUsableCodexCliAsync(cancellationToken))
                {
                    CliAccountAvailable = false;
                    StatusText = "请先登录可用的 Codex CLI 账户";
                    RestoreDataSourceSelections();
                    return;
                }

                CliAccountAvailable = true;
            }

            var settings = ToSettings() with
            {
                QuotaDataSource = requestedQuota,
                TokenUsageDataSource = requestedStatistics,
            };
            await ApplySettingsAsync(settings, cancellationToken);
            RestoreDataSourceSelections();

            if (runtime.Settings.QuotaDataSource == requestedQuota
                && runtime.Settings.TokenUsageDataSource == requestedStatistics)
            {
                StatusText = quotaSource is not null ? "额度来源已切换" : "统计来源已切换";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            RestoreDataSourceSelections();
            ReportDataSourceApplyFailure();
        }
        finally
        {
            DataSourceChangeInProgress = false;
            dataSourceGate.Release();
        }
    }

    private async Task<bool> HasUsableOAuthCredentialsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return account is not null
                && await account.HasUsableOAuthCredentialsAsync(cancellationToken);
        }
        catch (OAuthException)
        {
            return false;
        }
    }

    private void RestoreDataSourceSelections()
    {
        SelectedQuotaDataSource = runtime.Settings.QuotaDataSource;
        SelectedTokenUsageDataSource = runtime.Settings.TokenUsageDataSource;
        OnPropertyChanged(nameof(SelectedQuotaDataSourceIndex));
        OnPropertyChanged(nameof(SelectedTokenUsageDataSourceIndex));
    }

    [RelayCommand]
    private async Task RefreshAccountStatusAsync(CancellationToken cancellationToken)
    {
        if (account is null)
        {
            RefreshAccountStatusPresentation(null);
            StatusText = "账户服务不可用";
            return;
        }

        try
        {
            RefreshAccountStatusPresentation(await account.ReadStatusAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            StatusText = "账户状态读取失败";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditDataSources))]
    private async Task LoginOAuthAsync(CancellationToken cancellationToken)
    {
        if (account is null)
        {
            StatusText = "账户服务不可用";
            return;
        }

        OAuthLoginInProgress = true;
        LoginOAuthCommand.NotifyCanExecuteChanged();
        OAuthUserCodeText = string.Empty;
        OAuthVerificationUrl = string.Empty;
        StatusText = "正在准备 OAuth 设备登录…";
        using var loginCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        oauthLoginCancellationSource = loginCancellationSource;
        try
        {
            var device = await account.RequestOAuthDeviceCodeAsync(loginCancellationSource.Token);
            OAuthUserCodeText = device.UserCode;
            OAuthVerificationUrl = device.VerificationUrl;
            StatusText = $"请打开 {device.VerificationUrl} 并输入设备码 {device.UserCode}";
            var credentials = await account.CompleteOAuthLoginAsync(device, null, loginCancellationSource.Token);
            OAuthAvailable = true;
            OAuthAccountStatusText = string.IsNullOrWhiteSpace(credentials.Email) ? "已登录" : credentials.Email!;
            StatusText = "OAuth 登录成功";
        }
        catch (OperationCanceledException) when (loginCancellationSource.IsCancellationRequested)
        {
            OAuthAvailable = false;
            OAuthAccountStatusText = "未登录";
            StatusText = "OAuth 登录已取消";
        }
        catch (OAuthException error)
        {
            OAuthAvailable = false;
            OAuthAccountStatusText = error.Kind == OAuthFailureKind.DeviceAuthDisabled
                ? "设备码登录不可用"
                : "未登录";
            StatusText = "OAuth 登录失败";
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            OAuthAvailable = false;
            OAuthAccountStatusText = "未登录";
            StatusText = "OAuth 登录失败";
        }
        finally
        {
            if (ReferenceEquals(oauthLoginCancellationSource, loginCancellationSource))
            {
                oauthLoginCancellationSource = null;
            }

            OAuthUserCodeText = string.Empty;
            OAuthVerificationUrl = string.Empty;
            OAuthLoginInProgress = false;
            LoginOAuthCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CancelOAuthLogin() => oauthLoginCancellationSource?.Cancel();

    [RelayCommand]
    private async Task LogoutOAuthAsync(CancellationToken cancellationToken)
    {
        if (account is null)
        {
            return;
        }

        try
        {
            await account.LogoutOAuthAsync(cancellationToken);
            OAuthAvailable = false;
            OAuthAccountStatusText = "未登录";
            if (runtime.Settings.TokenUsageDataSource == TokenUsageDataSource.OAuth)
            {
                await SelectStatisticsDataSourceAsync(TokenUsageDataSource.Local, cancellationToken);
            }

            if (runtime.Settings.QuotaDataSource == QuotaDataSource.OAuth && CliAccountAvailable)
            {
                await SelectQuotaDataSourceAsync(QuotaDataSource.CodexCli, cancellationToken);
            }

            StatusText = "OAuth 已退出登录";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            StatusText = "OAuth 退出登录失败";
        }
    }

    public void ReportOAuthVerificationOpenFailure() => StatusText = "无法打开设备验证页";

    public void ReportOAuthCodeCopied() => StatusText = "设备登录代码已复制";

    public void ReportOAuthCodeCopyFailure() => StatusText = "无法复制设备登录代码";

    public void ReportDataSourceApplyFailure() => StatusText = "数据来源切换失败，已保留原来源";

    [RelayCommand]
    private void OpenOfficialUsage() => pageActions.OpenOfficialUsage();

    [RelayCommand]
    private void CopyDiagnostics() => pageActions.CopyDiagnostics();

    [RelayCommand]
    private async Task CheckForWindowsUpdatesAsync(CancellationToken cancellationToken)
    {
        if (UpdateCheckInProgress)
        {
            return;
        }

        if (updates is null || !updates.IsProduction)
        {
            UpdateCheckCompleted?.Invoke(
                this,
                new WindowsUpdateCheckResult(
                    WindowsUpdateCheckStatus.Disabled,
                    null,
                    "开发版本不检查正式更新",
                    null));
            return;
        }

        UpdateCheckInProgress = true;
        UpdateStatusText = "正在检查…";
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

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Interlocked.CompareExchange(ref downloadCancellationSource, linkedCancellation, null) is not null)
        {
            return WindowsUpdateDownloadResult.Failed("更新下载正在进行中。");
        }

        UpdateDownloadInProgress = true;
        Task<WindowsUpdateDownloadResult>? activeDownload = null;
        try
        {
            activeDownload = updates.DownloadAsync(linkedCancellation.Token);
            downloadTask = activeDownload;
            var result = await activeDownload;
            StatusText = result.Succeeded
                ? "更新已准备好"
                : result.WasCancelled ? "下载已取消" : result.ErrorMessage ?? "更新下载失败";
            return result;
        }
        finally
        {
            if (ReferenceEquals(downloadTask, activeDownload))
            {
                downloadTask = null;
            }
            Interlocked.CompareExchange(ref downloadCancellationSource, null, linkedCancellation);
            UpdateDownloadInProgress = false;
            OnPropertyChanged(nameof(CanDownloadWindowsUpdate));
        }
    }

    public async Task OpenWindowsUpdateInBrowserAsync(CancellationToken cancellationToken)
    {
        if (updates is null || !updates.IsProduction || !updates.CurrentResult.HasUpdate)
        {
            StatusText = "当前没有可用的 Windows 更新。";
            return;
        }

        downloadCancellationSource?.Cancel();
        var activeDownload = downloadTask;
        if (activeDownload is not null)
        {
            try
            {
                await activeDownload.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        try
        {
            await pageActions.OpenWindowsUpdateBrowserAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            StatusText = error.Message;
        }
    }

    [RelayCommand]
    private Task BrowserDownloadWindowsUpdateAsync(CancellationToken cancellationToken) =>
        OpenWindowsUpdateInBrowserAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(CanCancelWindowsUpdate))]
    private void CancelWindowsUpdate() => downloadCancellationSource?.Cancel();

    private bool CanCancelWindowsUpdate() => UpdateDownloadInProgress;

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
        var quotaDataSourceChanged = previous.QuotaDataSource != settings.QuotaDataSource;
        var tokenUsageDataSourceChanged = previous.TokenUsageDataSource != settings.TokenUsageDataSource;
        IsBusy = true;
        try
        {
            if (CanConfigureStartup && previous.StartWithWindows != settings.StartWithWindows)
            {
                await platform.SetStartupAsync(settings.StartWithWindows, cancellationToken);
            }

            await runtime.ApplySettingsAsync(settings, cancellationToken);
            if (quotaDataSourceChanged || tokenUsageDataSourceChanged)
            {
                DataSourcesChanged?.Invoke(
                    this,
                    new DataSourcesChangedEventArgs(quotaDataSourceChanged, tokenUsageDataSourceChanged));
            }
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
        Use24HourTime: true,
        PersistQuotaCache: PersistQuotaCache,
        RefreshMode: SelectedRefreshMode,
        RefreshOnPanelOpen: RefreshOnPanelOpen,
        RefreshOnNetworkRestore: RefreshOnNetworkRestore,
        Notifications: new NotificationSettings(
            NotifyRemaining50,
            NotifyRemaining20,
            NotifyRemaining10,
            NotifyAfterQuotaReset,
            NotifyResetCreditExpiry,
            ResetCreditExpiryLeadHours),
        ThemeMode: SelectedThemeMode,
        SilentStartup: SilentStartup,
        PhoneTokenSyncEnabled: PhoneTokenSyncEnabled,
        TokenRefreshMode: SelectedTokenRefreshMode,
        TokenRefreshOnPanelOpen: TokenRefreshOnPanelOpen,
        PersistTokenUsageCache: PersistTokenUsageCache,
        QuotaDataSource: runtime.Settings.QuotaDataSource,
        TokenUsageDataSource: runtime.Settings.TokenUsageDataSource));

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
            PersistQuotaCache = value.PersistQuotaCache;
            PersistTokenUsageCache = value.PersistTokenUsageCache;
            RefreshOnPanelOpen = value.RefreshOnPanelOpen;
            RefreshOnNetworkRestore = value.RefreshOnNetworkRestore;
            NotifyRemaining50 = value.EffectiveNotifications.Remaining50;
            NotifyRemaining20 = value.EffectiveNotifications.Remaining20;
            NotifyRemaining10 = value.EffectiveNotifications.Remaining10;
            NotifyAfterQuotaReset = value.EffectiveNotifications.ResetAfterCycle;
            NotifyResetCreditExpiry = value.EffectiveNotifications.NotifyResetCreditExpiry;
            ResetCreditExpiryLeadHours = value.EffectiveNotifications.ResetCreditExpiryLeadHours;
            OnPropertyChanged(nameof(SelectedResetCreditExpiryLeadIndex));
            SelectedRefreshMode = value.RefreshMode == RefreshMode.Auto
                ? RefreshMode.Every15Minutes
                : value.RefreshMode;
            SelectedTokenRefreshMode = value.TokenRefreshMode == RefreshMode.Auto
                ? RefreshMode.Every15Minutes
                : value.TokenRefreshMode;
            SelectedThemeMode = value.ThemeMode;
            SilentStartup = value.SilentStartup;
            PhoneTokenSyncEnabled = value.PhoneTokenSyncEnabled;
            TokenRefreshOnPanelOpen = value.TokenRefreshOnPanelOpen;
            SelectedQuotaDataSource = value.QuotaDataSource;
            SelectedTokenUsageDataSource = value.TokenUsageDataSource;
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

    partial void OnPersistQuotaCacheChanged(bool value) => QueueSettingsApply();

    partial void OnPersistTokenUsageCacheChanged(bool value) => QueueSettingsApply();

    partial void OnRefreshOnPanelOpenChanged(bool value) => QueueSettingsApply();

    partial void OnRefreshOnNetworkRestoreChanged(bool value) => QueueSettingsApply();

    partial void OnNotifyRemaining50Changed(bool value) => QueueSettingsApply();

    partial void OnNotifyRemaining20Changed(bool value) => QueueSettingsApply();

    partial void OnNotifyRemaining10Changed(bool value) => QueueSettingsApply();

    partial void OnNotifyAfterQuotaResetChanged(bool value) => QueueSettingsApply();

    partial void OnNotifyResetCreditExpiryChanged(bool value) => QueueSettingsApply();

    partial void OnResetCreditExpiryLeadHoursChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedResetCreditExpiryLeadIndex));
        QueueSettingsApply();
    }

    partial void OnSilentStartupChanged(bool value) => QueueSettingsApply();

    partial void OnPhoneTokenSyncEnabledChanged(bool value) => QueueSettingsApply();

    partial void OnTokenRefreshOnPanelOpenChanged(bool value) => QueueSettingsApply();

    partial void OnSelectedRefreshModeChanged(RefreshMode value) => QueueSettingsApply();

    partial void OnSelectedTokenRefreshModeChanged(RefreshMode value) => QueueSettingsApply();

    partial void OnSelectedThemeModeChanged(ThemeMode value) => QueueSettingsApply();

    partial void OnIsBusyChanged(bool value)
    {
        LoginOAuthCommand.NotifyCanExecuteChanged();
    }

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

    partial void OnAutoLaunchInstallerAfterDownloadChanged(bool value)
    {
        if (!suppressUpdateApply && updates is not null)
        {
            _ = ApplyAutoLaunchInstallerSettingAsync(value);
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
                AutoLaunchInstallerAfterDownload = false;
                UpdateStatusText = string.Empty;
                UpdateLastCheckText = "尚未检查";
                DownloadProgress = WindowsUpdateDownloadProgress.Idle;
                return;
            }

            AutomaticUpdateChecksEnabled = updates.AutomaticChecksEnabled;
            UpdateRemindersEnabled = updates.UpdateRemindersEnabled;
            AutoLaunchInstallerAfterDownload = updates.AutoLaunchInstallerAfterDownload;
            OnPropertyChanged(nameof(CanEditUpdateReminders));
            var result = updates.CurrentResult;
            DownloadProgress = updates.DownloadProgress;
            ApplyDownloadPresentation(result);
            var lastCheck = updates.LastAttemptUtc ?? updates.LastSuccessfulCheckUtc;
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

    private async Task ApplyAutoLaunchInstallerSettingAsync(bool value)
    {
        try
        {
            await updates!.SetAutoLaunchInstallerAfterDownloadAsync(value, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RefreshWindowsUpdateStatus();
            StatusText = "更新设置保存失败";
        }
    }

    internal static string FormatUpdateStatus(WindowsUpdateCheckResult result) => result.Status switch
    {
        WindowsUpdateCheckStatus.NotChecked => string.Empty,
        WindowsUpdateCheckStatus.Checking => "正在检查…",
        WindowsUpdateCheckStatus.Disabled => string.Empty,
        WindowsUpdateCheckStatus.Skipped => string.Empty,
        WindowsUpdateCheckStatus.UpToDate => "当前已是最新版本",
        WindowsUpdateCheckStatus.Available => $"发现新版本 {result.Release?.Version}",
        WindowsUpdateCheckStatus.NoRelease => "更新清单中没有有效的 Windows 安装包",
        WindowsUpdateCheckStatus.Failed => "检查更新失败",
        _ => "尚未检查",
    };

    internal static string FormatBytes(long bytes) => WindowsUpdateDownloadFormatting.FormatBytes(bytes);

    private void ApplyDownloadPresentation(WindowsUpdateCheckResult result)
    {
        UpdateStatusText = string.IsNullOrEmpty(DownloadProgressText)
            ? FormatUpdateStatus(result)
            : DownloadProgressText;
        OnPropertyChanged(nameof(HasDownloadProgress));
        OnPropertyChanged(nameof(IsDownloadProgressIndeterminate));
        OnPropertyChanged(nameof(DownloadProgressValue));
        OnPropertyChanged(nameof(DownloadProgressPercentageText));
        OnPropertyChanged(nameof(DownloadProgressText));
        OnPropertyChanged(nameof(DownloadProgressSizeText));
    }

    private void OnWindowsUpdateChanged(object? sender, EventArgs args)
    {
        RefreshWindowsUpdateStatus();
    }

    private void OnWindowsUpdateProgressChanged(object? sender, WindowsUpdateDownloadProgress progress)
    {
        DownloadProgress = progress;
        ApplyDownloadPresentation(updates?.CurrentResult ?? WindowsUpdateCheckResult.NotChecked);
    }

    private void OnTokenSyncChanged(object? sender, EventArgs args) => TokenSyncChanged?.Invoke(this, args);

    private void RefreshAccountStatusPresentation(WindowsAccountStatus? value)
    {
        if (value is null)
        {
            CliAccountStatusText = account is null ? "不可用" : "尚未检查";
            CliAccountAvailable = false;
            OAuthAccountStatusText = account is null ? "不可用" : "未检查";
            OAuthAvailable = false;
        }
        else
        {
            CliAccountAvailable = value.CodexCliAccount?.IsAuthenticated == true;
            CliAccountStatusText = value.CodexCliAccount is { IsAuthenticated: true } cli
                ? FormatCliAccount(cli)
                : value.CodexCliAvailable
                    ? "未登录，请先通过 Codex CLI 登录"
                    : "不可用";
            OAuthAvailable = value.OAuthAvailable;
            OAuthAccountStatusText = value.OAuthAccount is { } oauth
                ? FormatAccount(oauth)
                : "未登录";
        }
    }

    private static string FormatAccount(AccountReadResult value) =>
        string.IsNullOrWhiteSpace(value.Email)
            ? value.PlanType ?? "已连接"
            : value.Email!;

    private static string FormatCliAccount(AccountReadResult value) =>
        $"已登录 · {FormatAccount(value)}";
}
