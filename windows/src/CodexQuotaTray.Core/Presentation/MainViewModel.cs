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
    private bool isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    private bool showLoading;

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

    public ObservableCollection<QuotaWindowItemViewModel> Windows { get; } = [];

    public ObservableCollection<bool> LoadingWindows { get; } = [true];

    public bool HasPlanBadge => !string.IsNullOrWhiteSpace(PlanBadge);

    public bool HasWindows => Windows.Count != 0;

    public bool ShowContent => !ShowLoading;

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
        BeginRefreshPresentation(showLoading: !HasWindows, Windows.Count);
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
            if (ShowLoading)
            {
                SetLoadingWindowCount(state.Windows.Count);
            }

            pendingRefreshResult = state;
            EndRefreshPresentationAfterMinimum();
            return;
        }

        var startsContentLoading = state.IsRefreshing && state.Windows.Count == 0;
        if (state.IsRefreshing && (!IsRefreshing || (startsContentLoading && !ShowLoading)))
        {
            BeginRefreshPresentation(startsContentLoading, Windows.Count);
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
        SyncWindows(state.Windows);

        OnPropertyChanged(nameof(HasWindows));
    }

    private void SyncWindows(IReadOnlyList<QuotaWindowView> incoming)
    {
        var existingByKey = Windows.ToDictionary(window => window.LocalKey, StringComparer.Ordinal);
        var incomingKeys = new HashSet<string>(StringComparer.Ordinal);
        var desiredItems = new List<QuotaWindowItemViewModel>(incoming.Count);

        foreach (var window in incoming)
        {
            if (!incomingKeys.Add(window.LocalKey))
            {
                throw new InvalidOperationException($"Duplicate quota window LocalKey: {window.LocalKey}");
            }

            if (existingByKey.TryGetValue(window.LocalKey, out var existing))
            {
                existing.UpdateFrom(window);
                desiredItems.Add(existing);
            }
            else
            {
                desiredItems.Add(new QuotaWindowItemViewModel(window));
            }
        }

        for (var index = Windows.Count - 1; index >= 0; index--)
        {
            if (!incomingKeys.Contains(Windows[index].LocalKey))
            {
                Windows.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < desiredItems.Count; targetIndex++)
        {
            var desiredItem = desiredItems[targetIndex];
            if (targetIndex < Windows.Count && ReferenceEquals(Windows[targetIndex], desiredItem))
            {
                continue;
            }

            var currentIndex = Windows.IndexOf(desiredItem);
            if (currentIndex >= 0)
            {
                Windows.Move(currentIndex, targetIndex);
            }
            else
            {
                Windows.Insert(targetIndex, desiredItem);
            }
        }
    }

    private void BeginRefreshPresentation(bool showLoading, int loadingWindowCount)
    {
        refreshPresentationCompletion?.Cancel();
        refreshPresentationCompletion?.Dispose();
        refreshPresentationCompletion = null;
        pendingRefreshResult = null;
        Interlocked.Increment(ref refreshPresentationRevision);
        refreshPresentationStartedTimestamp = Stopwatch.GetTimestamp();
        IsRefreshing = true;
        ShowLoading = showLoading;
        if (showLoading)
        {
            SetLoadingWindowCount(loadingWindowCount);
        }
    }

    private void SetLoadingWindowCount(int count)
    {
        count = Math.Max(1, count);
        if (LoadingWindows.Count == count)
        {
            return;
        }

        LoadingWindows.Clear();
        for (var index = 0; index < count; index++)
        {
            LoadingWindows.Add(true);
        }
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
            ShowLoading = false;
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
                ShowLoading = false;
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
