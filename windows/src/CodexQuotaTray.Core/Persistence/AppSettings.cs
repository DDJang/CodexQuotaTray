using System.Text.Json.Serialization;
using CodexQuotaTray.Core.Models;
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
    bool ResetAfterCycle = true,
    bool NotifyResetCreditExpiry = false,
    int ResetCreditExpiryLeadHours = 24);

public sealed record AppSettings(
    bool StartWithWindows = false,
    bool ShowRemainingPercent = true,
    bool Use24HourTime = true,
    bool PersistQuotaCache = true,
    RefreshMode RefreshMode = RefreshMode.Every15Minutes,
    bool RefreshOnPanelOpen = true,
    bool RefreshOnNetworkRestore = true,
    NotificationSettings? Notifications = null,
    ThemeMode ThemeMode = ThemeMode.Dark,
    bool SilentStartup = true,
    bool PhoneTokenSyncEnabled = false,
    RefreshMode TokenRefreshMode = RefreshMode.Every15Minutes,
    bool TokenRefreshOnPanelOpen = true,
    bool PersistTokenUsageCache = true)
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
    DateTimeOffset? ResetCreditEarliestExpiryUtc = null,
    IReadOnlyList<ResetCreditView>? ResetCreditCredits = null);

public sealed record QuotaCacheWindow(
    string SourceSlot,
    long UsedPercent,
    long RemainingPercent,
    bool PercentageReliable,
    long? WindowDurationMinutes,
    DateTimeOffset? ResetAtUtc,
    string? BucketId = null);

public sealed record AlertStateDocument(
    int SchemaVersion,
    IReadOnlyList<int> BaselineThresholds,
    Dictionary<string, AlertWindowState> Windows,
    bool ResetAlertBaselineEstablished = false,
    Dictionary<string, ResetCreditAlertState>? ResetCredits = null);

public sealed record AlertWindowState(
    string PseudonymousKey,
    long? WindowDurationMinutes,
    DateTimeOffset? ResetAtUtc,
    int? LastReliableRemaining,
    IReadOnlyList<int> HandledThresholds,
    DateTimeOffset? LastResetAlertCycleUtc = null,
    bool? ResetAlertCycleConsumed = null,
    bool ResetAlertAwaitingCycleMetadata = false);

public sealed record ResetCreditAlertState(
    DateTimeOffset? LastSeenUtc = null,
    DateTimeOffset? ExpiresAtUtc = null,
    bool Notified = false);
