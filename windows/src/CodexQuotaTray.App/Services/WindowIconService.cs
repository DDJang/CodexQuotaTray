using Microsoft.UI.Windowing;

namespace CodexQuotaTray.App.Services;

internal static class WindowIconService
{
    internal static readonly string LightThemeWindowIconPath = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "WindowIcon.ico");

    internal static readonly string TrayIconPath = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "AppIcon.ico");

    internal static readonly string AppNotificationIconPath = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "AppIcon.png");

    internal static bool TrySetIcon(AppWindow appWindow, bool isDarkTheme)
    {
        var iconPath = isDarkTheme ? TrayIconPath : LightThemeWindowIconPath;
        if (!File.Exists(iconPath))
        {
            return false;
        }

        try
        {
            appWindow.SetIcon(iconPath);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
