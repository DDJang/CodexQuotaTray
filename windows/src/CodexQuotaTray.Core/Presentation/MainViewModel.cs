using System.Collections.ObjectModel;
using CodexQuotaTray.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexQuotaTray.Core.Presentation;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IUiStateProvider stateProvider;
    private readonly IExternalNavigation navigation;
    private readonly bool stateEventsAuthoritative;

    [ObservableProperty]
    private string title = "Codex";

    [ObservableProperty]
    private string? planBadge;

    [ObservableProperty]
    private string statusText = "正在连接 Codex…";

    [ObservableProperty]
    private StatusTone statusTone = StatusTone.Refreshing;

    [ObservableProperty]
    private ResetCreditViewState resetCredits = new(ResetCreditKind.Unavailable);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool isRefreshing;

    [ObservableProperty]
    private bool isPrototype;

    public MainViewModel(
        IUiStateProvider stateProvider,
        IExternalNavigation navigation,
        bool stateEventsAuthoritative = false)
    {
        this.stateProvider = stateProvider;
        this.navigation = navigation;
        this.stateEventsAuthoritative = stateEventsAuthoritative;
    }

    public ObservableCollection<QuotaWindowView> Windows { get; } = [];

    public bool HasPlanBadge => !string.IsNullOrWhiteSpace(PlanBadge);

    public bool HasWindows => Windows.Count != 0;

    partial void OnPlanBadgeChanged(string? value) => OnPropertyChanged(nameof(HasPlanBadge));

    [RelayCommand]
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await stateProvider.GetSnapshotAsync(cancellationToken);
        if (!stateEventsAuthoritative)
        {
            Apply(snapshot);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsRefreshing = true;
        StatusText = "正在获取额度…";
        StatusTone = StatusTone.Refreshing;
        try
        {
            var snapshot = await stateProvider.RefreshAsync(cancellationToken);
            if (!stateEventsAuthoritative)
            {
                Apply(snapshot);
            }
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private void OpenUsage() => navigation.OpenOfficialUsage();

    public void ReportStartupFailure()
    {
        StatusText = "! 无法启动 Codex 连接 · 点击刷新重试";
        StatusTone = StatusTone.Error;
        IsRefreshing = false;
        IsPrototype = false;
    }

    public void ApplySnapshot(AppUiState state) => Apply(state);

    private bool CanRefresh() => !IsRefreshing;

    private void Apply(AppUiState state)
    {
        Title = state.Title;
        PlanBadge = state.PlanBadge;
        StatusText = state.StatusText;
        StatusTone = state.StatusTone;
        ResetCredits = state.ResetCredits;
        IsRefreshing = state.IsRefreshing;
        IsPrototype = state.IsPrototype;
        Windows.Clear();
        foreach (var window in state.Windows)
        {
            Windows.Add(window);
        }

        OnPropertyChanged(nameof(HasWindows));
    }
}
