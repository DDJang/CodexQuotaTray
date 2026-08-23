using System.Text.Json;
using CodexQuotaTray.Core.Runtime;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.Core.Persistence;

public sealed class SettingsService(JsonFileStore store, PreviewDataPaths paths)
{
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var document = await LoadDocumentAsync(paths.Settings, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return AppSettings.Defaults;
            }

            return Normalize(Migrate(document.RootElement));
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return AppSettings.Defaults;
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) =>
        store.SaveAsync(paths.Settings, Normalize(settings), cancellationToken);

    public Task ResetAsync(CancellationToken cancellationToken) => SaveAsync(AppSettings.Defaults, cancellationToken);

    public static AppSettings Normalize(AppSettings settings) => settings with
    {
        Use24HourTime = true,
        RefreshMode = settings.RefreshMode == RefreshMode.Auto
            ? RefreshMode.Every15Minutes
            : settings.RefreshMode,
        TokenRefreshMode = settings.TokenRefreshMode == RefreshMode.Auto
            ? RefreshMode.Every15Minutes
            : settings.TokenRefreshMode,
        Notifications = NormalizeNotifications(settings.EffectiveNotifications),
    };

    private static NotificationSettings NormalizeNotifications(NotificationSettings settings) =>
        settings with
        {
            ResetCreditExpiryLeadHours = settings.ResetCreditExpiryLeadHours switch
            {
                6 => 6,
                1 => 1,
                _ => 24,
            },
        };

    private static AppSettings Migrate(JsonElement root)
    {
        var defaults = AppSettings.Defaults;
        var notifications = root.TryGetProperty("notifications", out var notificationElement)
            ? ParseNotifications(notificationElement)
            : ParseLegacyNotifications(root);
        return new AppSettings(
            StartWithWindows: Boolean(root, "startWithWindows", defaults.StartWithWindows),
            ShowRemainingPercent: Boolean(root, "showRemainingPercent", defaults.ShowRemainingPercent),
            Use24HourTime: Boolean(root, "use24HourTime", defaults.Use24HourTime),
            PersistQuotaCache: Boolean(root, "persistQuotaCache", defaults.PersistQuotaCache),
            RefreshMode: ParseRefreshMode(root),
            RefreshOnPanelOpen: Boolean(root, "refreshOnPanelOpen", defaults.RefreshOnPanelOpen),
            RefreshOnNetworkRestore: Boolean(root, "refreshOnNetworkRestore", defaults.RefreshOnNetworkRestore),
            Notifications: notifications,
            ThemeMode: EnumValue(root, "themeMode", ThemeMode.System),
            SilentStartup: Boolean(root, "silentStartup", true),
            PhoneTokenSyncEnabled: Boolean(root, "phoneTokenSyncEnabled", false),
            TokenRefreshMode: EnumValue(root, "tokenRefreshMode", defaults.TokenRefreshMode),
            TokenRefreshOnPanelOpen: Boolean(root, "tokenRefreshOnPanelOpen", defaults.TokenRefreshOnPanelOpen),
            PersistTokenUsageCache: Boolean(root, "persistTokenUsageCache", defaults.PersistTokenUsageCache),
            QuotaDataSource: EnumValue(root, "quotaDataSource", defaults.QuotaDataSource),
            TokenUsageDataSource: EnumValue(root, "tokenUsageDataSource", defaults.TokenUsageDataSource));
    }

    private static NotificationSettings ParseNotifications(JsonElement value) => new(
        Boolean(value, "remaining50", false),
        Boolean(value, "remaining20", true),
        Boolean(value, "remaining10", true),
        Boolean(value, "resetAfterCycle", true),
        Boolean(value, "notifyResetCreditExpiry", false),
        Integer(value, "resetCreditExpiryLeadHours", 24));

    private static NotificationSettings ParseLegacyNotifications(JsonElement root)
    {
        var twenty = Boolean(root, "notifyRemaining20", true);
        var five = Boolean(root, "notifyRemaining5", true);
        var exhausted = Boolean(root, "notifyExhausted", true);
        return new NotificationSettings(false, twenty, five || exhausted, true, false, 24);
    }

    private static RefreshMode ParseRefreshMode(JsonElement root)
    {
        if (root.TryGetProperty("refreshMode", out var mode) && mode.ValueKind == JsonValueKind.String
            && Enum.TryParse<RefreshMode>(mode.GetString(), true, out var parsed))
        {
            return parsed;
        }

        if (root.TryGetProperty("refreshMinutes", out var minutes) && minutes.TryGetInt32(out var value))
        {
            return value switch
            {
                5 => RefreshMode.Every5Minutes,
                15 => RefreshMode.Every15Minutes,
                30 => RefreshMode.Every30Minutes,
                _ => RefreshMode.Every15Minutes,
            };
        }

        return RefreshMode.Every15Minutes;
    }

    private static bool Boolean(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static int Integer(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static T EnumValue<T>(JsonElement root, string name, T fallback)
        where T : struct, Enum =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && Enum.TryParse<T>(value.GetString(), true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : fallback;

    private static async Task<JsonDocument?> LoadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (data.Length > JsonFileStore.MaximumBytes)
        {
            throw new InvalidDataException("The settings document is too large.");
        }

        return JsonDocument.Parse(data);
    }
}

public class PreviewPersistence(JsonFileStore store, PreviewDataPaths paths)
{
    public async Task<QuotaCacheDocument?> LoadQuotaCacheAsync(
        CancellationToken cancellationToken,
        QuotaDataSource source = QuotaDataSource.CodexCli)
    {
        try
        {
            var value = await store.LoadAsync<QuotaCacheDocument>(paths.QuotaCacheFor(source), cancellationToken).ConfigureAwait(false);
            return value?.FormatVersion == 1
                && value.Source == source
                && value.Windows.Count <= 32
                    ? value
                    : null;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public virtual Task SaveQuotaCacheAsync(QuotaCacheDocument value, CancellationToken cancellationToken) =>
        store.SaveAsync(paths.QuotaCacheFor(value.Source), value, cancellationToken);

    public virtual Task<bool> SaveQuotaCacheWithCommitAsync(
        QuotaCacheDocument value,
        CancellationToken cancellationToken,
        SemaphoreSlim commitGate,
        Func<bool> canCommit,
        Action onCommitted) =>
        store.SaveWithCommitAsync(paths.QuotaCacheFor(value.Source), value, cancellationToken, commitGate, canCommit, onCommitted);

    public Task ClearQuotaCacheAsync(QuotaDataSource source = QuotaDataSource.CodexCli)
    {
        var path = paths.QuotaCacheFor(source);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public async Task<TokenUsageSnapshot?> LoadTokenUsageCacheAsync(
        CancellationToken cancellationToken,
        TokenUsageDataSource source = TokenUsageDataSource.Local)
    {
        try
        {
            var value = await store.LoadAsync<TokenUsageSnapshot>(paths.TokenUsageCacheFor(source), cancellationToken).ConfigureAwait(false);
            return value?.SchemaVersion == 1
                && value.Summary is not null
                && value.Days is { Count: <= 366 }
                && value.Source == source
                    ? value
                    : null;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task SaveTokenUsageCacheAsync(TokenUsageSnapshot value, CancellationToken cancellationToken) =>
        store.SaveAsync(paths.TokenUsageCacheFor(value.Source), value, cancellationToken);

    public Task ClearTokenUsageCacheAsync(TokenUsageDataSource source = TokenUsageDataSource.Local)
    {
        var path = paths.TokenUsageCacheFor(source);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public async Task<AlertStateDocument?> LoadAlertStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var value = await store.LoadAsync<AlertStateDocument>(paths.AlertState, cancellationToken).ConfigureAwait(false);
            return value?.SchemaVersion == 1 && value.Windows.Count <= 32 ? value : null;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public virtual Task SaveAlertStateAsync(AlertStateDocument value, CancellationToken cancellationToken) =>
        store.SaveAsync(paths.AlertState, value, cancellationToken);

    public virtual Task<bool> SaveAlertStateWithCommitAsync(
        AlertStateDocument value,
        CancellationToken cancellationToken,
        SemaphoreSlim commitGate,
        Func<bool> canCommit,
        Action onCommitted) =>
        store.SaveWithCommitAsync(paths.AlertState, value, cancellationToken, commitGate, canCommit, onCommitted);
}
