using System.Collections.ObjectModel;
using System.Diagnostics;
using CodexQuotaTray.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexQuotaTray.Core.Presentation;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IUiStateProvider stateProvider;
    private readonly IExternalNavigation navigation;
    private readonly bool stateEventsAuthoritative;
    private long refreshPresentationStartedTimestamp;
    private long refreshPresentationRevision;
    private CancellationTokenSource? refreshPresentationCompletion;
    private AppUiState? pendingRefreshResult;

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
    [NotifyPropertyChangedFor(nameof(ShowQuotaLoading))]
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

    public bool ShowQuotaLoading => IsRefreshing && !HasWindows;

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
        BeginRefreshPresentation();
        StatusText = "正在刷新…";
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
            EndRefreshPresentationAfterMinimum();
        }
    }

    [RelayCommand]
    private void OpenUsage() => navigation.OpenOfficialUsage();

    public void ReportStartupFailure()
    {
        StatusText = "刷新失败：无法启动 Codex 连接 · 点击刷新重试";
        StatusTone = StatusTone.Error;
        EndRefreshPresentationAfterMinimum();
        IsPrototype = false;
    }

    public void ApplySnapshot(AppUiState state) => Apply(state);

    private bool CanRefresh() => !IsRefreshing;

    private void Apply(AppUiState state)
    {
        if (!state.IsRefreshing && IsRefreshing)
        {
            pendingRefreshResult = state;
            EndRefreshPresentationAfterMinimum();
            return;
        }

        var startsEmptyContentRefresh = state.IsRefreshing && state.Windows.Count == 0 && HasWindows;
        if (state.IsRefreshing && (!IsRefreshing || startsEmptyContentRefresh))
        {
            BeginRefreshPresentation();
        }

        ApplyCore(state);
    }

    private void ApplyCore(AppUiState state)
    {
        Title = state.Title;
        PlanBadge = state.PlanBadge;
        StatusText = state.StatusText;
        StatusTone = state.StatusTone;
        ResetCredits = state.ResetCredits;
        IsPrototype = state.IsPrototype;
        Windows.Clear();
        foreach (var window in state.Windows)
        {
            Windows.Add(window);
        }

        OnPropertyChanged(nameof(HasWindows));
        OnPropertyChanged(nameof(ShowQuotaLoading));
    }

    private void BeginRefreshPresentation()
    {
        refreshPresentationCompletion?.Cancel();
        refreshPresentationCompletion?.Dispose();
        refreshPresentationCompletion = null;
        pendingRefreshResult = null;
        Interlocked.Increment(ref refreshPresentationRevision);
        refreshPresentationStartedTimestamp = Stopwatch.GetTimestamp();
        IsRefreshing = true;
    }

    private void EndRefreshPresentationAfterMinimum()
    {
        if (!IsRefreshing || refreshPresentationCompletion is not null)
        {
            return;
        }

        var remaining = RefreshPresentationPolicy.Remaining(refreshPresentationStartedTimestamp);
        if (remaining == TimeSpan.Zero)
        {
            ApplyPendingRefreshResult();
            IsRefreshing = false;
            return;
        }

        var revision = Interlocked.Read(ref refreshPresentationRevision);
        var cancellation = new CancellationTokenSource();
        refreshPresentationCompletion = cancellation;
        _ = CompleteRefreshPresentationAsync(revision, remaining, cancellation);
    }

    private async Task CompleteRefreshPresentationAsync(
        long revision,
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);
            if (revision == Interlocked.Read(ref refreshPresentationRevision))
            {
                ApplyPendingRefreshResult();
                IsRefreshing = false;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(refreshPresentationCompletion, cancellation))
            {
                refreshPresentationCompletion = null;
            }

            cancellation.Dispose();
        }
    }

    private void ApplyPendingRefreshResult()
    {
        if (pendingRefreshResult is not { } result)
        {
            return;
        }

        pendingRefreshResult = null;
        ApplyCore(result);
    }
}
