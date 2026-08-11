using System.Text.Json;
using CodexQuotaTray.Core.Runtime;

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

            return Migrate(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return AppSettings.Defaults;
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) =>
        store.SaveAsync(paths.Settings, settings with { Notifications = settings.EffectiveNotifications }, cancellationToken);

    public Task ResetAsync(CancellationToken cancellationToken) => SaveAsync(AppSettings.Defaults, cancellationToken);

    private static AppSettings Migrate(JsonElement root)
    {
        var defaults = AppSettings.Defaults;
        var notifications = root.TryGetProperty("notifications", out var notificationElement)
            ? ParseNotifications(notificationElement)
            : ParseLegacyNotifications(root);
        return new AppSettings(
            Boolean(root, "startWithWindows", defaults.StartWithWindows),
            Boolean(root, "showRemainingPercent", defaults.ShowRemainingPercent),
            Boolean(root, "use24HourTime", defaults.Use24HourTime),
            Boolean(root, "persistQuotaCache", defaults.PersistQuotaCache),
            ParseRefreshMode(root),
            Boolean(root, "refreshOnNetworkRestore", defaults.RefreshOnNetworkRestore),
            notifications,
            EnumValue(root, "themeMode", ThemeMode.System),
            Boolean(root, "silentStartup", true),
            Boolean(root, "phoneTokenSyncEnabled", false));
    }

    private static NotificationSettings ParseNotifications(JsonElement value) => new(
        Boolean(value, "remaining50", false),
        Boolean(value, "remaining20", true),
        Boolean(value, "remaining10", true),
        Boolean(value, "resetAfterCycle", true));

    private static NotificationSettings ParseLegacyNotifications(JsonElement root)
    {
        var twenty = Boolean(root, "notifyRemaining20", true);
        var five = Boolean(root, "notifyRemaining5", true);
        var exhausted = Boolean(root, "notifyExhausted", true);
        return new NotificationSettings(false, twenty, five || exhausted, true);
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
                _ => RefreshMode.Auto,
            };
        }

        return RefreshMode.Auto;
    }

    private static bool Boolean(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static T EnumValue<T>(JsonElement root, string name, T fallback)
        where T : struct, Enum =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && Enum.TryParse<T>(value.GetString(), true, out var parsed)
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
    public async Task<QuotaCacheDocument?> LoadQuotaCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            var value = await store.LoadAsync<QuotaCacheDocument>(paths.QuotaCache, cancellationToken).ConfigureAwait(false);
            return value?.FormatVersion == 1 && value.Windows.Count <= 32 ? value : null;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public virtual Task SaveQuotaCacheAsync(QuotaCacheDocument value, CancellationToken cancellationToken) =>
        store.SaveAsync(paths.QuotaCache, value, cancellationToken);

    public virtual Task<bool> SaveQuotaCacheWithCommitAsync(
        QuotaCacheDocument value,
        CancellationToken cancellationToken,
        SemaphoreSlim commitGate,
        Func<bool> canCommit,
        Action onCommitted) =>
        store.SaveWithCommitAsync(paths.QuotaCache, value, cancellationToken, commitGate, canCommit, onCommitted);

    public Task ClearQuotaCacheAsync()
    {
        if (File.Exists(paths.QuotaCache))
        {
            File.Delete(paths.QuotaCache);
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

public sealed class ProductionDataImporter(JsonFileStore store)
{
    public async Task<int> ImportAsync(string productionRoot, PreviewDataPaths preview, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(preview.Root);
        var count = 0;
        var production = new PreviewDataPaths(productionRoot);
        if (File.Exists(production.Settings))
        {
            var settings = await new SettingsService(store, production).LoadAsync(cancellationToken).ConfigureAwait(false);
            await store.SaveAsync(preview.Settings, settings with { Notifications = settings.EffectiveNotifications }, cancellationToken).ConfigureAwait(false);
            count++;
        }

        if (File.Exists(production.QuotaCache))
        {
            try
            {
                var cache = await store.LoadAsync<QuotaCacheDocument>(production.QuotaCache, cancellationToken).ConfigureAwait(false);
                if (cache is { FormatVersion: 1 } && cache.Windows.Count <= 32)
                {
                    await store.SaveAsync(preview.QuotaCache, cache, cancellationToken).ConfigureAwait(false);
                    count++;
                }
            }
            catch (Exception error) when (error is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
            }
        }

        if (File.Exists(production.AlertState))
        {
            try
            {
                var state = await store.LoadAsync<AlertStateDocument>(production.AlertState, cancellationToken).ConfigureAwait(false);
                if (state is { SchemaVersion: 1 } && state.Windows.Count <= 32)
                {
                    await store.SaveAsync(preview.AlertState, state, cancellationToken).ConfigureAwait(false);
                    count++;
                }
            }
            catch (Exception error) when (error is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
            }
        }

        return count;
    }
}
