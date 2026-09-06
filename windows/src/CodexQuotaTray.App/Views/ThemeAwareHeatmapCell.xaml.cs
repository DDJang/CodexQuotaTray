using System.Numerics;
using CodexQuotaTray.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CodexQuotaTray.App.Views;

public sealed partial class ThemeAwareHeatmapCell : UserControl
{
    public static readonly DependencyProperty BucketProperty = DependencyProperty.Register(
        nameof(Bucket),
        typeof(int),
        typeof(ThemeAwareHeatmapCell),
        new PropertyMetadata(0, OnBucketChanged));

    private bool highlightActive;
    private bool highlightEmpty;
    private bool highlightHighContrast;

    public ThemeAwareHeatmapCell()
    {
        InitializeComponent();
        CenterPoint = new Vector3(8.5f, 8.5f, 0f);
        Scale = Vector3.One;
        Loaded += OnLoaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public int Bucket
    {
        get => (int)GetValue(BucketProperty);
        set => SetValue(BucketProperty, value);
    }

    internal Brush? EffectiveBackground => CellSurface?.Background;

    internal Brush? EffectiveBorderBrush => CellSurface?.BorderBrush;

    internal void ApplyHighlight(Brush derivedBrush, bool isEmpty, bool isHighContrast)
    {
        highlightActive = true;
        highlightEmpty = isEmpty;
        highlightHighContrast = isHighContrast;
        ApplyHighlightState(derivedBrush);
    }

    internal void ClearHighlight()
    {
        highlightActive = false;
        VisualStateManager.GoToState(this, "HighlightNone", false);
        BorderBrush = null;
        BorderThickness = new Thickness(0);
    }

    internal void RefreshTheme(bool? isHighContrast = null)
    {
        if (isHighContrast is { } highContrast)
        {
            highlightHighContrast = highContrast;
        }

        ApplyBucketState();
        if (highlightActive)
        {
            ApplyHighlightState(CreateDerivedHighlightBrush(EffectiveBackground));
        }
    }

    private static void OnBucketChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((ThemeAwareHeatmapCell)dependencyObject).ApplyBucketState();

    private void OnLoaded(object sender, RoutedEventArgs args) => RefreshTheme();

    private void OnActualThemeChanged(FrameworkElement sender, object args) => RefreshTheme();

    private void ApplyBucketState()
    {
        VisualStateManager.GoToState(this, Bucket switch
        {
            1 => "Bucket1",
            2 => "Bucket2",
            3 => "Bucket3",
            4 => "Bucket4",
            _ => "Bucket0",
        }, false);
        if (ThemeBrushResolver.TryResolve(this, ThemeResourceKeyPolicy.Heatmap(Bucket)) is { } brush)
        {
            Background = brush;
        }
    }

    private void ApplyHighlightState(Brush derivedBrush)
    {
        if (highlightEmpty)
        {
            VisualStateManager.GoToState(this, "HighlightEmpty", false);
            if (ThemeBrushResolver.TryResolve(this, "TokenHeatmapEmptyCellHighlightBrush") is { } brush)
            {
                BorderBrush = brush;
            }
            return;
        }

        if (highlightHighContrast)
        {
            VisualStateManager.GoToState(this, "HighlightHighContrast", false);
            if (ThemeBrushResolver.TryResolve(this, "TokenHeatmapCellBorderBrush") is { } brush)
            {
                BorderBrush = brush;
            }
            return;
        }

        VisualStateManager.GoToState(this, "HighlightNone", false);
        BorderBrush = derivedBrush;
    }

    private static Brush CreateDerivedHighlightBrush(Brush? background)
    {
        if (background is not SolidColorBrush solidColorBrush)
        {
            return background ?? new SolidColorBrush();
        }

        var color = solidColorBrush.Color;
        return new SolidColorBrush(Color.FromArgb(
            color.A,
            LiftChannel(color.R),
            LiftChannel(color.G),
            LiftChannel(color.B)));
    }

    private static byte LiftChannel(byte channel)
    {
        const double whiteMix = 0.24;
        return (byte)Math.Round(channel + ((255 - channel) * whiteMix), MidpointRounding.AwayFromZero);
    }
}
