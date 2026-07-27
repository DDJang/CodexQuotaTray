using CodexQuotaTray.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace CodexQuotaTray.App;

public sealed class QuotaToneBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Resource(value switch
        {
            QuotaTone.Warning => "WarningQuotaBrush",
            QuotaTone.Critical => "CriticalQuotaBrush",
            QuotaTone.Unavailable => "UnavailableQuotaBrush",
            _ => "AccentQuotaBrush",
        });

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}

public sealed class StatusToneBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (Brush)Application.Current.Resources[value switch
        {
            StatusTone.Success => "SuccessStatusBrush",
            StatusTone.Refreshing => "RefreshingStatusBrush",
            StatusTone.Warning => "WarningStatusBrush",
            StatusTone.Error => "ErrorStatusBrush",
            _ => "NeutralStatusBrush",
        }];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is true;
        if (string.Equals(parameter as string, "Inverse", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}
