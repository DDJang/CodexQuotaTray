using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace CodexQuotaTray.App.Services;

internal static class ThemeBrushResolver
{
    internal static Brush? TryResolve(FrameworkElement element, string key)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return null;
        }

        var scope = ThemeResourceKeyPolicy.Scope(
            new AccessibilitySettings().HighContrast,
            element.ActualTheme == ElementTheme.Dark);
        if (TryResolveThemeDictionary(element.Resources, scope.ToString(), key, out var brush)
            || TryResolveThemeDictionary(resources, scope.ToString(), key, out brush))
        {
            return brush;
        }

        if (scope != ThemeResourceScope.Light
            && (TryResolveThemeDictionary(element.Resources, ThemeResourceScope.Light.ToString(), key, out brush)
                || TryResolveThemeDictionary(resources, ThemeResourceScope.Light.ToString(), key, out brush)))
        {
            return brush;
        }

        return resources.TryGetValue(key, out var value) ? value as Brush : null;
    }

    private static bool TryResolveThemeDictionary(
        ResourceDictionary resources,
        string scope,
        string key,
        out Brush? brush)
    {
        brush = null;
        if (resources.ThemeDictionaries.ContainsKey(scope)
            && resources.ThemeDictionaries[scope] is ResourceDictionary dictionary
            && dictionary.TryGetValue(key, out var value)
            && value is Brush resolved)
        {
            brush = resolved;
            return true;
        }

        for (var index = resources.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            if (TryResolveThemeDictionary(resources.MergedDictionaries[index], scope, key, out brush))
            {
                return true;
            }
        }

        return false;
    }
}
