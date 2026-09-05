using System.Diagnostics;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexQuotaTray.App.Views;

public sealed partial class QuotaView : UserControl
{
    public QuotaView()
    {
        InitializeComponent();
        Loaded += (_, _) => LogThemeState("loaded");
        ActualThemeChanged += (_, _) => LogThemeState("actual-theme-changed");
    }

    internal Grid ContentBottomBoundary => FooterRow;

    internal void RefreshTheme()
    {
        for (var index = 0; ; index++)
        {
            if (QuotaWindowsRepeater.TryGetElement(index) is not StackPanel item)
            {
                break;
            }

            if (item.Children.Count > 0
                && item.Children[0] is Grid header
                && header.Children.OfType<QuotaToneDisplay>().FirstOrDefault() is { } display)
            {
                display.RefreshTheme();
            }

            if (item.Children.Count > 1
                && item.Children[1] is QuotaProgressVisual progress)
            {
                progress.RefreshTheme();
            }
        }
    }

    [Conditional("DEBUG")]
    private void LogThemeState(string stage)
    {
        if (QuotaWindowsRepeater.TryGetElement(0) is not StackPanel item
            || item.Children.Count < 2
            || item.Children[0] is not Grid header
            || header.Children.OfType<QuotaToneDisplay>().FirstOrDefault() is not { } display
            || item.Children[1] is not QuotaProgressVisual progress)
        {
            return;
        }

        ThemeDebugTelemetry.LogQuota(
            stage,
            this,
            ThemeResourceKeyPolicy.Quota(display.Tone),
            display.CurrentBrush,
            progress.CurrentIndicatorBrush);
    }
}
