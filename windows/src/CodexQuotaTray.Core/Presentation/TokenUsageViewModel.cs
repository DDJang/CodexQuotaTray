using System.Collections.ObjectModel;
using System.Globalization;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Runtime;
using CodexQuotaTray.Core.TokenUsage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexQuotaTray.Core.Presentation;

public sealed partial class TokenUsageViewModel : ObservableObject
{
    private const int HeatmapWeeks = 17;
    private readonly Func<CancellationToken, Task<TokenUsageSnapshot>> scan;
    private TokenUsageSnapshot? snapshot;
    private int refreshRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool isRefreshing;

    [ObservableProperty]
    private string statusText = "尚未刷新";

    [ObservableProperty]
    private StatusTone statusTone = StatusTone.Neutral;

    [ObservableProperty]
    private bool hasData;

    [ObservableProperty]
    private bool hasNoData;

    [ObservableProperty]
    private bool hasErrorWithoutData;

    [ObservableProperty]
    private bool showLoading;

    public bool ShowContent => HasData && !ShowLoading;

    public bool ShowEmpty => HasNoData && !ShowLoading;

    public bool ShowError => HasErrorWithoutData && !ShowLoading;

    [ObservableProperty]
    private string todayTokens = "0";

    [ObservableProperty]
    private string last7DaysTokens = "0";

    [ObservableProperty]
    private string last30DaysTokens = "0";

    [ObservableProperty]
    private string lifetimeTokens = "0";

    [ObservableProperty]
    private string peakDailyTokens = "0";

    [ObservableProperty]
    private string currentStreak = "0 天";

    [ObservableProperty]
    private string longestStreak = "0 天";

    public TokenUsageViewModel(Func<CancellationToken, Task<TokenUsageSnapshot>> scan)
    {
        this.scan = scan;
    }

    public ObservableCollection<TokenHeatmapCell> HeatmapCells { get; } = [];

    public bool HasLoaded => snapshot is not null;

    public DateTimeOffset? LastAttemptUtc { get; private set; }

    public Task RefreshNowAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    public void RestoreSnapshot(TokenUsageSnapshot value)
    {
        LastAttemptUtc = value.GeneratedAtUtc;
        Apply(value);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref refreshRunning, 1) != 0)
        {
            return;
        }

        LastAttemptUtc = DateTimeOffset.UtcNow;
        IsRefreshing = true;
        ShowLoading = snapshot is null;
        StatusText = "正在刷新…";
        StatusTone = StatusTone.Refreshing;
        HasErrorWithoutData = false;
        try
        {
            Apply(await scan(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            StatusText = snapshot is null ? "刷新失败" : "刷新失败 · 显示上次数据";
            StatusTone = snapshot is null ? StatusTone.Error : StatusTone.Warning;
            HasErrorWithoutData = snapshot is null;
        }
        finally
        {
            IsRefreshing = false;
            ShowLoading = false;
            Volatile.Write(ref refreshRunning, 0);
        }
    }

    internal void Apply(TokenUsageSnapshot value, DateOnly? today = null)
    {
        var summary = value.Summary;
        var localToday = today ?? DateOnly.FromDateTime(DateTime.Now);
        var cells = TokenHeatmap.Build(value.Days, localToday, HeatmapWeeks);

        snapshot = value;
        TodayTokens = TokenUsageFormatter.Format(summary.TodayTokens);
        Last7DaysTokens = TokenUsageFormatter.Format(summary.Last7DaysTokens);
        Last30DaysTokens = TokenUsageFormatter.Format(summary.Last30DaysTokens);
        LifetimeTokens = TokenUsageFormatter.Format(summary.LifetimeTokens);
        PeakDailyTokens = TokenUsageFormatter.Format(summary.PeakDailyTokens);
        CurrentStreak = $"{summary.CurrentStreak} 天";
        LongestStreak = $"{summary.LongestStreak} 天";

        HeatmapCells.Clear();
        foreach (var cell in cells)
        {
            HeatmapCells.Add(cell);
        }

        HasData = summary.LifetimeTokens > 0;
        HasNoData = !HasData;
        HasErrorWithoutData = false;
        StatusText = HasData ? $"更新于 {value.GeneratedAtUtc.ToLocalTime():HH:mm}" : "暂无 Token 数据";
        StatusTone = HasData ? StatusTone.Success : StatusTone.Neutral;

        OnPropertyChanged(nameof(HasLoaded));
    }

    private bool CanRefresh() => !IsRefreshing;

    partial void OnHasDataChanged(bool value) => OnPropertyChanged(nameof(ShowContent));

    partial void OnHasNoDataChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    partial void OnHasErrorWithoutDataChanged(bool value) => OnPropertyChanged(nameof(ShowError));

    partial void OnShowLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowContent));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
    }
}

public static class TokenUsageRefreshPolicy
{
    private static readonly TimeSpan PanelOpenDeduplicationInterval = TimeSpan.FromSeconds(10);

    public static TimeSpan? Interval(RefreshMode mode) => mode switch
    {
        RefreshMode.Every5Minutes => TimeSpan.FromMinutes(5),
        RefreshMode.Auto or RefreshMode.Every15Minutes => TimeSpan.FromMinutes(15),
        RefreshMode.Every30Minutes => TimeSpan.FromMinutes(30),
        _ => null,
    };

    public static bool IsDue(RefreshMode mode, DateTimeOffset? lastAttemptUtc, DateTimeOffset nowUtc)
    {
        var interval = Interval(mode);
        return interval is not null
            && (lastAttemptUtc is null || nowUtc - lastAttemptUtc.Value >= interval.Value);
    }

    public static bool ShouldRefreshOnPanelOpen(
        bool enabled,
        DateTimeOffset? lastAttemptUtc,
        DateTimeOffset nowUtc) =>
        enabled
        && (lastAttemptUtc is null || nowUtc - lastAttemptUtc.Value >= PanelOpenDeduplicationInterval);
}

public sealed record TokenHeatmapCell(
    DateOnly Date,
    long TotalTokens,
    int Bucket,
    string TokenText,
    string DateText,
    string AutomationText);

public static class TokenUsageFormatter
{
    public static string Format(long value)
    {
        var positive = Math.Max(0, value);
        return positive switch
        {
            < 1_000 => positive.ToString(CultureInfo.InvariantCulture),
            < 1_000_000 => Compact(positive, 1_000d, "K"),
            < 1_000_000_000 => Compact(positive, 1_000_000d, "M"),
            _ => Compact(positive, 1_000_000_000d, "B"),
        };
    }

    private static string Compact(long value, double divisor, string suffix)
    {
        var number = value / divisor;
        var format = number >= 100 || number % 1d == 0d ? "0" : "0.0";
        return number.ToString(format, CultureInfo.InvariantCulture) + suffix;
    }
}

public static class TokenHeatmap
{
    public static IReadOnlyList<TokenHeatmapCell> Build(
        IReadOnlyList<TokenUsageDay> days,
        DateOnly today,
        int weeks)
    {
        var start = StartOfWeek(today).AddDays(-7 * (weeks - 1));
        var byDate = days.Where(day => day.Date >= start && day.Date <= today).ToDictionary(day => day.Date);
        var nonZero = byDate.Values.Select(day => day.TotalTokens).Where(value => value > 0).Order().ToArray();
        var result = new List<TokenHeatmapCell>(weeks * 7);
        for (var index = 0; index < weeks * 7; index++)
        {
            var date = start.AddDays(index);
            var tokens = date <= today && byDate.TryGetValue(date, out var day) ? day.TotalTokens : 0;
            var tokenText = $"{tokens.ToString("N0", CultureInfo.InvariantCulture)} Token";
            result.Add(new TokenHeatmapCell(
                date,
                tokens,
                Bucket(tokens, nonZero),
                tokenText,
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                $"{FormatDate(date, today.Year)} · {tokenText}"));
        }

        return result;
    }

    public static int Bucket(long value, IReadOnlyList<long> sortedNonZeroValues)
    {
        if (value <= 0 || sortedNonZeroValues.Count == 0)
        {
            return 0;
        }

        var last = sortedNonZeroValues.Count - 1;
        var q1 = sortedNonZeroValues[(int)(last * 0.25)];
        var q2 = sortedNonZeroValues[(int)(last * 0.50)];
        var q3 = sortedNonZeroValues[(int)(last * 0.75)];
        return value <= q1 ? 1 : value <= q2 ? 2 : value <= q3 ? 3 : 4;
    }

    private static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(int)date.DayOfWeek);

    private static string FormatDate(DateOnly date, int currentYear)
    {
        return date.Year == currentYear
            ? $"{date.Month} 月 {date.Day} 日"
            : $"{date.Year} 年 {date.Month} 月 {date.Day} 日";
    }
}
