using CodexQuotaTray.Core.Models;
using CodexQuotaTray.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexQuotaTray.App.Views;

public sealed partial class QuotaToneDisplay : UserControl
{
    public static readonly DependencyProperty PercentStyleProperty = DependencyProperty.Register(
        nameof(PercentStyle),
        typeof(Style),
        typeof(QuotaToneDisplay),
        new PropertyMetadata(null));

    public static readonly DependencyProperty PercentTextProperty = DependencyProperty.Register(
        nameof(PercentText),
        typeof(string),
        typeof(QuotaToneDisplay),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone),
        typeof(QuotaTone),
        typeof(QuotaToneDisplay),
        new PropertyMetadata(QuotaTone.Accent, OnToneChanged));

    public QuotaToneDisplay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public Style? PercentStyle
    {
        get => (Style?)GetValue(PercentStyleProperty);
        set => SetValue(PercentStyleProperty, value);
    }

    public string PercentText
    {
        get => (string)GetValue(PercentTextProperty);
        set => SetValue(PercentTextProperty, value);
    }

    public QuotaTone Tone
    {
        get => (QuotaTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    internal Brush? CurrentBrush => PercentTextBlock?.Foreground;

    internal void RefreshTheme() => ApplyToneState();

    private static void OnToneChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((QuotaToneDisplay)dependencyObject).ApplyToneState();

    private void OnLoaded(object sender, RoutedEventArgs args) => ApplyToneState();

    private void OnActualThemeChanged(FrameworkElement sender, object args) => ApplyToneState();

    private void ApplyToneState()
    {
        VisualStateManager.GoToState(this, Tone switch
        {
            QuotaTone.Warning => "Warning",
            QuotaTone.Critical => "Critical",
            QuotaTone.Unavailable => "Unavailable",
            _ => "Healthy",
        }, false);
        if (ThemeBrushResolver.TryResolve(this, ThemeResourceKeyPolicy.Quota(Tone)) is { } brush)
        {
            PercentTextBlock.Foreground = brush;
        }
        ThemeDebugTelemetry.LogQuota(
            "tone-state",
            this,
            ThemeResourceKeyPolicy.Quota(Tone),
            PercentTextBlock.Foreground,
            null);
    }
}
