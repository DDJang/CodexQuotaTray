using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace CodexQuotaTray.App.Views;

internal sealed record SettingsPageNavigation(
    FrameworkElement PageContent,
    object DataContext,
    string? Title = null,
    Action? Back = null);

public sealed class SettingsContentPage : Page
{
    private readonly StackPanel contentRoot;
    private readonly FrameworkElement pageContent;

    internal SettingsContentPage(SettingsPageNavigation navigation, double? fromHorizontalOffset = null)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        DataContext = navigation.DataContext;
        if (fromHorizontalOffset is { } offset)
        {
            Transitions =
            [
                new EntranceThemeTransition
                {
                    FromHorizontalOffset = offset,
                    FromVerticalOffset = 0,
                    IsStaggeringEnabled = false,
                },
            ];
        }

        pageContent = navigation.PageContent;
        pageContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        contentRoot = new StackPanel
        {
            Padding = new Thickness(24, 0, 24, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        if (!string.IsNullOrWhiteSpace(navigation.Title))
        {
            contentRoot.Children.Add(CreateHeader(navigation.Title, navigation.Back));
        }

        contentRoot.Children.Add(pageContent);
        Content = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            Content = contentRoot,
        };
    }

    internal void DetachPageContent()
    {
        _ = contentRoot.Children.Remove(pageContent);
        Content = null;
    }

    private static Grid CreateHeader(string title, Action? back)
    {
        var header = new Grid
        {
            Margin = new Thickness(0, 16, 0, 8),
            ColumnSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var backButton = new Button
        {
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE72B", FontSize = 15 },
        };
        AutomationProperties.SetName(backButton, "返回设置分类");
        backButton.Click += (_, _) => back?.Invoke();
        header.Children.Add(backButton);

        var titleText = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        Grid.SetColumn(titleText, 1);
        header.Children.Add(titleText);
        return header;
    }
}
