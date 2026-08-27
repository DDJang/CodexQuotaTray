using CodexQuotaTray.Core.Presentation;
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

    public static readonly DependencyProperty IndicatorBrushProperty = DependencyProperty.Register(
        nameof(IndicatorBrush),
        typeof(Brush),
        typeof(QuotaProgressVisual),
        new PropertyMetadata(null));

    public QuotaProgressVisual()
    {
        InitializeComponent();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush? IndicatorBrush
    {
        get => (Brush?)GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
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
