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

        return ApplyBuiltIn(window, selected);
    }

    internal BackdropKind ApplyForSettings(Window window)
    {
        var highContrast = new AccessibilitySettings().HighContrast;
        var transparency = new UISettings().AdvancedEffectsEnabled;
        var selected = BackdropPolicy.SelectForSettings(
            MicaController.IsSupported(),
            transparency,
            highContrast);
        return ApplyBuiltIn(window, selected);
    }

    private BackdropKind ApplyBuiltIn(Window window, BackdropKind selected)
    {
        if (applied == selected)
        {
            return selected;
        }

        DisposeController();
        try
        {
            window.SystemBackdrop = selected switch
            {
                BackdropKind.Mica => new MicaBackdrop { Kind = MicaKind.Base },
                _ => null,
            };
            applied = selected;
        }
        catch
        {
            if (selected == BackdropKind.DesktopAcrylic && MicaController.IsSupported())
            {
                try
                {
                    window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                    applied = BackdropKind.Mica;
                    return applied.Value;
                }
                catch
                {
                }
            }

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
            acrylicController.TintColor = Color.FromArgb(255, 238, 240, 242);
            acrylicController.FallbackColor = Color.FromArgb(255, 243, 243, 243);
            acrylicController.TintOpacity = 0.56f;
            acrylicController.LuminosityOpacity = 0.78f;
        }
        else
        {
            acrylicController.TintColor = Color.FromArgb(255, 48, 52, 56);
            acrylicController.FallbackColor = Color.FromArgb(255, 48, 52, 56);
            acrylicController.TintOpacity = 0.52f;
            acrylicController.LuminosityOpacity = 0.64f;
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
