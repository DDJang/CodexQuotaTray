using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexQuotaTray.App.Views;

public sealed partial class QuotaProgressVisual : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(QuotaProgressVisual),
        new PropertyMetadata(0d, OnGeometryPropertyChanged));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone),
        typeof(QuotaTone),
        typeof(QuotaProgressVisual),
        new PropertyMetadata(QuotaTone.Accent, OnToneChanged));

    public QuotaProgressVisual()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public QuotaTone Tone
    {
        get => (QuotaTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    internal Brush? CurrentIndicatorBrush => Indicator?.Background;

    internal void RefreshTheme() => ApplyToneState();

    private static void OnToneChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((QuotaProgressVisual)dependencyObject).ApplyToneState();

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
            Indicator.Background = brush;
        }
        ThemeDebugTelemetry.LogQuota(
            "indicator-state",
            this,
            ThemeResourceKeyPolicy.Quota(Tone),
            null,
            Indicator.Background);
    }

    private static void OnGeometryPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((QuotaProgressVisual)dependencyObject).UpdateIndicatorWidth();
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs args) => UpdateIndicatorWidth();

    private void UpdateIndicatorWidth()
    {
        if (Track is null || Indicator is null)
        {
            return;
        }

        Indicator.Width = QuotaProgressGeometry.Calculate(Track.ActualWidth, Value).Width;
    }
}
