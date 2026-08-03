using Microsoft.UI.Windowing;

namespace CodexQuotaTray.App.Services;

internal static class WindowIconService
{
    internal static readonly string IconPath = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "AppIcon.ico");

    internal static bool TrySetIcon(AppWindow appWindow)
    {
        if (!File.Exists(IconPath))
        {
            return false;
        }

        try
        {
            appWindow.SetIcon(IconPath);
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
