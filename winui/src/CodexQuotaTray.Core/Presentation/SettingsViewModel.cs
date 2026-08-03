using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexQuotaTray.Core.Presentation;

public interface ISettingsPlatformActions
{
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
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool isBusy;

    public SettingsViewModel(
        IQuotaRuntimeControl runtime,
        ISettingsPlatformActions platform,
        ISettingsPageActions pageActions)
    {
        this.runtime = runtime;
        this.platform = platform;
        this.pageActions = pageActions;
        Load(runtime.Settings);
    }

    public IReadOnlyList<RefreshMode> RefreshModes { get; } = Enum.GetValues<RefreshMode>();

    public IReadOnlyList<ThemeMode> ThemeModes { get; } = Enum.GetValues<ThemeMode>();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var previous = runtime.Settings;
        IsBusy = true;
        try
        {
            await platform.SetStartupAsync(StartWithWindows, cancellationToken);
            await runtime.ApplySettingsAsync(ToSettings(), cancellationToken);
            StatusText = "设置已保存";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Load(previous);
            StatusText = "保存失败，已恢复原设置";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResetAsync(CancellationToken cancellationToken)
    {
        Load(AppSettings.Defaults);
        await SaveAsync(cancellationToken);
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

    private bool CanSave() => !IsBusy;

    private AppSettings ToSettings() => new(
        StartWithWindows,
        ShowRemainingPercent,
        Use24HourTime,
        PersistQuotaCache,
        SelectedRefreshMode,
        RefreshOnNetworkRestore,
        new NotificationSettings(NotifyRemaining50, NotifyRemaining20, NotifyRemaining10, NotifyAfterQuotaReset),
        SelectedThemeMode,
        SilentStartup);

    private void Load(AppSettings value)
    {
        StartWithWindows = value.StartWithWindows;
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
}
