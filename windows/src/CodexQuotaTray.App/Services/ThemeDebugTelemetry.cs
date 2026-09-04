using System.Diagnostics;
using Microsoft.Win32;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace CodexQuotaTray.App.Services;

internal static class ThemeDebugTelemetry
{
    [Conditional("DEBUG")]
    internal static void LogMainWindow(
        string stage,
        FrameworkElement contentRoot,
        FrameworkElement quotaView,
        FrameworkElement? tokenUsageView,
        Brush? panelSurface,
        Brush? statusBrush,
        string? statusKey)
    {
        Write(
            $"stage={stage} "
            + $"systemRequested={ReadSystemTheme()} "
            + $"appRequested={contentRoot.RequestedTheme} "
            + $"appActual={contentRoot.ActualTheme} "
            + $"quotaActual={quotaView.ActualTheme} "
            + $"tokenActual={tokenUsageView?.ActualTheme.ToString() ?? "not-created"} "
            + $"statusKey={statusKey ?? "none"} "
            + $"statusArgb={Describe(statusBrush)} "
            + $"panelSurfaceArgb={Describe(panelSurface)} "
            + $"highContrast={new AccessibilitySettings().HighContrast}");
    }

    [Conditional("DEBUG")]
    internal static void LogQuota(
        string stage,
        FrameworkElement quotaView,
        string resourceKey,
        Brush? percentBrush,
        Brush? indicatorBrush)
    {
        Write(
            $"stage={stage} quotaActual={quotaView.ActualTheme} "
            + $"resourceKey={resourceKey} "
            + $"percentArgb={Describe(percentBrush)} "
            + $"indicatorArgb={Describe(indicatorBrush)}");
    }

    [Conditional("DEBUG")]
    internal static void LogTokenHeatmap(
        string stage,
        FrameworkElement tokenUsageView,
        int bucket,
        Brush? background,
        Brush? borderBrush)
    {
        Write(
            $"stage={stage} tokenActual={tokenUsageView.ActualTheme} "
            + $"resourceKey={ThemeResourceKeyPolicy.Heatmap(bucket)} "
            + $"bucket={Math.Clamp(bucket, 0, 4)} "
            + $"backgroundArgb={Describe(background)} "
            + $"borderArgb={Describe(borderBrush)}");
    }

    [Conditional("DEBUG")]
    internal static void LogTokenTheme(
        string stage,
        FrameworkElement tokenUsageView,
        int realizedCells)
    {
        Write(
            $"stage={stage} tokenActual={tokenUsageView.ActualTheme} "
            + $"realizedCells={realizedCells} "
            + $"highContrast={new AccessibilitySettings().HighContrast}");
    }

    private static string ReadSystemTheme()
    {
        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return personalize?.GetValue("AppsUseLightTheme") switch
            {
                int value when value == 0 => "Dark",
                int value when value == 1 => "Light",
                _ => "Unknown",
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return $"Unknown({error.GetType().Name})";
        }
    }

    private static string Describe(Brush? brush) => brush switch
    {
        SolidColorBrush solid => $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
        null => "null",
        _ => brush.GetType().Name,
    };

    private static void Write(string line)
    {
        var message = $"pid={Environment.ProcessId} {line}";
        Debug.WriteLine($"Theme diagnostics: {message}");
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), $"CodexQuotaTray-theme-debug-{Environment.ProcessId}.log"),
                message + Environment.NewLine);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Debug.WriteLine($"Theme diagnostics file failed: {error.GetType().Name}");
        }
    }
}
