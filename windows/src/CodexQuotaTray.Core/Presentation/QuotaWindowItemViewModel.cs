using CodexQuotaTray.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexQuotaTray.Core.Presentation;

public sealed class QuotaWindowItemViewModel : ObservableObject
{
    private string name = string.Empty;
    private int usedPercent;
    private int remainingPercent;
    private int displayPercent;
    private int progressValue;
    private long? windowDurationMinutes;
    private DateTimeOffset? resetAtUtc;
    private string resetAt = string.Empty;
    private string resetRelative = string.Empty;
    private QuotaTone tone;
    private bool isPercentageReliable;
    private bool isAvailable;
    private bool isStale;

    public QuotaWindowItemViewModel(QuotaWindowView source)
    {
        ArgumentNullException.ThrowIfNull(source);
        LocalKey = source.LocalKey;
        UpdateFrom(source);
    }

    public string LocalKey { get; }

    public string Name
    {
        get => name;
        private set => SetProperty(ref name, value);
    }

    public int UsedPercent
    {
        get => usedPercent;
        private set => SetProperty(ref usedPercent, value);
    }

    public int RemainingPercent
    {
        get => remainingPercent;
        private set => SetProperty(ref remainingPercent, value);
    }

    public int DisplayPercent
    {
        get => displayPercent;
        private set
        {
            if (SetProperty(ref displayPercent, value))
            {
                OnPropertyChanged(nameof(PercentText));
            }
        }
    }

    public int ProgressValue
    {
        get => progressValue;
        private set => SetProperty(ref progressValue, value);
    }

    public long? WindowDurationMinutes
    {
        get => windowDurationMinutes;
        private set => SetProperty(ref windowDurationMinutes, value);
    }

    public DateTimeOffset? ResetAtUtc
    {
        get => resetAtUtc;
        private set => SetProperty(ref resetAtUtc, value);
    }

    public string ResetAt
    {
        get => resetAt;
        private set => SetProperty(ref resetAt, value);
    }

    public string ResetRelative
    {
        get => resetRelative;
        private set => SetProperty(ref resetRelative, value);
    }

    public QuotaTone Tone
    {
        get => tone;
        private set => SetProperty(ref tone, value);
    }

    public bool IsPercentageReliable
    {
        get => isPercentageReliable;
        private set => SetProperty(ref isPercentageReliable, value);
    }

    public bool IsAvailable
    {
        get => isAvailable;
        private set => SetProperty(ref isAvailable, value);
    }

    public bool IsStale
    {
        get => isStale;
        private set => SetProperty(ref isStale, value);
    }

    public string PercentText => $"{DisplayPercent}%";

    public void UpdateFrom(QuotaWindowView source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(LocalKey, source.LocalKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("The quota window LocalKey cannot change.", nameof(source));
        }

        Name = source.Name;
        UsedPercent = source.UsedPercent;
        RemainingPercent = source.RemainingPercent;
        DisplayPercent = source.DisplayPercent;
        ProgressValue = source.ProgressValue;
        WindowDurationMinutes = source.WindowDurationMinutes;
        ResetAtUtc = source.ResetAtUtc;
        ResetAt = source.ResetAt;
        ResetRelative = source.ResetRelative;
        Tone = source.Tone;
        IsPercentageReliable = source.IsPercentageReliable;
        IsAvailable = source.IsAvailable;
        IsStale = source.IsStale;
    }
}
