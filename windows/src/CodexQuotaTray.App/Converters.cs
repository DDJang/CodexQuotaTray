using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Persistence;
using PersistenceThemeMode = CodexQuotaTray.Core.Persistence.ThemeMode;
using RuntimeRefreshMode = CodexQuotaTray.Core.Runtime.RefreshMode;
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
            _ => "HealthyQuotaBrush",
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

public sealed class TokenHeatmapBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (Brush)Application.Current.Resources[$"TokenHeatmap{Math.Clamp(value is int bucket ? bucket : 0, 0, 4)}Brush"];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class RefreshModeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        RuntimeRefreshMode.Auto => "每 15 分钟",
        RuntimeRefreshMode.Every5Minutes => "每 5 分钟",
        RuntimeRefreshMode.Every15Minutes => "每 15 分钟",
        RuntimeRefreshMode.Every30Minutes => "每 30 分钟",
        RuntimeRefreshMode.ManualOnly => "关闭后台刷新",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class ThemeModeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        PersistenceThemeMode.System => "跟随系统",
        PersistenceThemeMode.Light => "浅色",
        PersistenceThemeMode.Dark => "深色",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class QuotaDataSourceDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        QuotaDataSource.CodexCli => "OpenAI（Codex CLI）",
        QuotaDataSource.OAuth => "OpenAI（OAuth）",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class TokenUsageDataSourceDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        TokenUsageDataSource.Local => "本机历史",
        TokenUsageDataSource.CodexCli => "OpenAI（Codex CLI）",
        TokenUsageDataSource.OAuth => "OpenAI（OAuth）",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
