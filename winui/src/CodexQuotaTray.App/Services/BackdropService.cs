using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace CodexQuotaTray.App.Services;

internal sealed class BackdropService
{
    private BackdropKind? applied;

    internal BackdropKind Apply(Window window)
    {
        var highContrast = new AccessibilitySettings().HighContrast;
        var transparency = new UISettings().AdvancedEffectsEnabled;
        var selected = BackdropPolicy.Select(
            DesktopAcrylicController.IsSupported(),
            MicaController.IsSupported(),
            transparency,
            highContrast);
        if (applied == selected)
        {
            return selected;
        }

        try
        {
            window.SystemBackdrop = selected switch
            {
                BackdropKind.DesktopAcrylic => new DesktopAcrylicBackdrop(),
                BackdropKind.Mica => new MicaBackdrop(),
                _ => null,
            };
            applied = selected;
        }
        catch
        {
            window.SystemBackdrop = null;
            applied = BackdropKind.Opaque;
        }

        return applied.Value;
    }
}
