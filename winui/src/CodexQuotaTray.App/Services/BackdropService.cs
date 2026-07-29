using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT;

namespace CodexQuotaTray.App.Services;

internal sealed class BackdropService : IDisposable
{
    private BackdropKind? applied;
    private DesktopAcrylicController? acrylicController;
    private SystemBackdropConfiguration? configuration;

    internal BackdropKind Apply(Window window)
    {
        var highContrast = new AccessibilitySettings().HighContrast;
        var transparency = new UISettings().AdvancedEffectsEnabled;
        var selected = BackdropPolicy.Select(
            DesktopAcrylicController.IsSupported(),
            MicaController.IsSupported(),
            transparency,
            highContrast);

        if (selected == BackdropKind.DesktopAcrylic)
        {
            try
            {
                ApplyAcrylic(window);
                applied = selected;
                return selected;
            }
            catch
            {
                DisposeController();
                selected = MicaController.IsSupported() ? BackdropKind.Mica : BackdropKind.Opaque;
            }
        }

        if (applied == selected)
        {
            return selected;
        }

        DisposeController();
        try
        {
            window.SystemBackdrop = selected == BackdropKind.Mica ? new MicaBackdrop() : null;
            applied = selected;
        }
        catch
        {
            window.SystemBackdrop = null;
            applied = BackdropKind.Opaque;
        }

        return applied.Value;
    }

    private void ApplyAcrylic(Window window)
    {
        var theme = (window.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
        configuration ??= new SystemBackdropConfiguration
        {
            IsInputActive = true,
        };
        configuration.Theme = theme switch
        {
            ElementTheme.Light => SystemBackdropTheme.Light,
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            _ => SystemBackdropTheme.Default,
        };

        if (acrylicController is null)
        {
            window.SystemBackdrop = null;
            acrylicController = new DesktopAcrylicController();
            acrylicController.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
            acrylicController.SetSystemBackdropConfiguration(configuration);
        }

        if (theme == ElementTheme.Light)
        {
            acrylicController.TintColor = Color.FromArgb(255, 220, 234, 248);
            acrylicController.FallbackColor = Color.FromArgb(255, 232, 240, 248);
            acrylicController.TintOpacity = 0.22f;
            acrylicController.LuminosityOpacity = 0.55f;
        }
        else
        {
            acrylicController.TintColor = Color.FromArgb(255, 28, 45, 64);
            acrylicController.FallbackColor = Color.FromArgb(255, 28, 38, 50);
            acrylicController.TintOpacity = 0.18f;
            acrylicController.LuminosityOpacity = 0.48f;
        }
    }

    private void DisposeController()
    {
        if (acrylicController is not null)
        {
            acrylicController.Dispose();
            acrylicController = null;
        }

        configuration = null;
    }

    public void Dispose()
    {
        DisposeController();
        applied = null;
    }
}
