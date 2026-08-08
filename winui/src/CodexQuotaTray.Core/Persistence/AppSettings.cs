using System.Text.Json.Serialization;
using CodexQuotaTray.Core.Runtime;

namespace CodexQuotaTray.Core.Persistence;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public sealed record NotificationSettings(
    bool Remaining50 = false,
    bool Remaining20 = true,
    bool Remaining10 = true,
    bool ResetAfterCycle = true);

public sealed record AppSettings(
    bool StartWithWindows = false,
    bool ShowRemainingPercent = true,
    bool Use24HourTime = true,
    bool PersistQuotaCache = true,
    RefreshMode RefreshMode = RefreshMode.Auto,
    bool RefreshOnNetworkRestore = true,
    NotificationSettings? Notifications = null,
    ThemeMode ThemeMode = ThemeMode.System,
    bool SilentStartup = true,
    bool PhoneTokenSyncEnabled = false)
{
    [JsonIgnore]
    public NotificationSettings EffectiveNotifications => Notifications ?? new();

    public static AppSettings Defaults { get; } = new(Notifications: new());
}

public sealed record QuotaCacheDocument(
    int FormatVersion,
    DateTimeOffset LastSuccessUtc,
    string? PlanType,
    IReadOnlyList<QuotaCacheWindow> Windows,
    long? ResetCreditAvailableCount = null,
    DateTimeOffset? ResetCreditEarliestExpiryUtc = null);

public sealed record QuotaCacheWindow(
    string SourceSlot,
    long UsedPercent,
    long RemainingPercent,
    bool PercentageReliable,
    long? WindowDurationMinutes,
    DateTimeOffset? ResetAtUtc);

public sealed record AlertStateDocument(
    int SchemaVersion,
    IReadOnlyList<int> BaselineThresholds,
    Dictionary<string, AlertWindowState> Windows,
    bool ResetAlertBaselineEstablished = false);

public sealed record AlertWindowState(
    string PseudonymousKey,
    long? WindowDurationMinutes,
    DateTimeOffset? ResetAtUtc,
    int? LastReliableRemaining,
    IReadOnlyList<int> HandledThresholds,
    DateTimeOffset? LastResetAlertCycleUtc = null);
