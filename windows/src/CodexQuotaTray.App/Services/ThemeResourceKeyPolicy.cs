using CodexQuotaTray.Core.Models;

namespace CodexQuotaTray.App.Services;

public enum ThemeResourceScope
{
    Light,
    Dark,
    HighContrast,
}

public static class ThemeResourceKeyPolicy
{
    public static string Quota(QuotaTone tone) => tone switch
    {
        QuotaTone.Warning => "WarningQuotaBrush",
        QuotaTone.Critical => "CriticalQuotaBrush",
        QuotaTone.Unavailable => "UnavailableQuotaBrush",
        _ => "HealthyQuotaBrush",
    };

    public static string Status(StatusTone tone) => tone switch
    {
        StatusTone.Success => "SuccessStatusBrush",
        StatusTone.Refreshing => "RefreshingStatusBrush",
        StatusTone.Warning => "WarningStatusBrush",
        StatusTone.Error => "ErrorStatusBrush",
        _ => "NeutralStatusBrush",
    };

    public static string Heatmap(int bucket) =>
        $"TokenHeatmap{Math.Clamp(bucket, 0, 4)}Brush";

    public static string PanelSurface(BackdropKind backdrop) =>
        backdrop == BackdropKind.Opaque
            ? "MainWindowOpaqueSurfaceBrush"
            : "MainWindowSurfaceBrush";

    public static ThemeResourceScope Scope(bool isHighContrast, bool isDark) =>
        isHighContrast
            ? ThemeResourceScope.HighContrast
            : isDark
                ? ThemeResourceScope.Dark
                : ThemeResourceScope.Light;

    public static bool TryResolve<T>(
        string key,
        ThemeResourceScope scope,
        IReadOnlyDictionary<ThemeResourceScope, IReadOnlyDictionary<string, T>> dictionaries,
        out T value)
    {
        if (dictionaries.TryGetValue(scope, out var active)
            && active.TryGetValue(key, out value!))
        {
            return true;
        }

        if (scope == ThemeResourceScope.Dark
            && dictionaries.TryGetValue(ThemeResourceScope.Light, out var light)
            && light.TryGetValue(key, out value!))
        {
            return true;
        }

        value = default!;
        return false;
    }
}
