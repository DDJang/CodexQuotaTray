using CodexQuotaTray.Core.Updates;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace CodexQuotaTray.App.Views;

internal static class ReleaseNotesMarkdownRenderer
{
    internal static FrameworkElement Create(string markdown)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var block in ReleaseNotesMarkdown.Parse(markdown))
        {
            panel.Children.Add(CreateBlock(block));
        }

        return panel;
    }

    private static FrameworkElement CreateBlock(ReleaseNotesBlock block) => block.Kind switch
    {
        ReleaseNotesBlockKind.Heading => CreateRichText(block, block.Level switch
        {
            1 => 19,
            2 => 17,
            _ => 15,
        }, emphasized: true),
        ReleaseNotesBlockKind.UnorderedListItem => CreateRichText(block, 14, emphasized: false, prefix: "• "),
        ReleaseNotesBlockKind.OrderedListItem => CreateRichText(block, 14, emphasized: false, prefix: $"{block.ListIndex}. "),
        ReleaseNotesBlockKind.Quote => new Border
        {
            BorderBrush = ResourceBrush("SettingsCardBorderBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            Child = CreateRichText(block, 14, emphasized: false),
        },
        ReleaseNotesBlockKind.CodeBlock => new Border
        {
            Background = ResourceBrush("SettingsCardSurfaceBrush"),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new TextBlock
            {
                Text = block.Inlines.FirstOrDefault()?.Text ?? string.Empty,
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        },
        _ => CreateRichText(block, 14, emphasized: false),
    };

    private static RichTextBlock CreateRichText(
        ReleaseNotesBlock block,
        double fontSize,
        bool emphasized,
        string prefix = "")
    {
        var richText = new RichTextBlock
        {
            FontSize = fontSize,
            FontWeight = emphasized ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
        };
        var paragraph = new Paragraph();
        if (prefix.Length > 0)
        {
            paragraph.Inlines.Add(new Run { Text = prefix });
        }

        foreach (var inline in block.Inlines)
        {
            AddInline(paragraph, inline);
        }

        richText.Blocks.Add(paragraph);
        return richText;
    }

    private static void AddInline(Paragraph paragraph, ReleaseNotesInline inline)
    {
        switch (inline.Kind)
        {
            case ReleaseNotesInlineKind.Bold:
                var bold = new Bold();
                bold.Inlines.Add(new Run { Text = inline.Text });
                paragraph.Inlines.Add(bold);
                break;
            case ReleaseNotesInlineKind.Italic:
                var italic = new Italic();
                italic.Inlines.Add(new Run { Text = inline.Text });
                paragraph.Inlines.Add(italic);
                break;
            case ReleaseNotesInlineKind.InlineCode:
                paragraph.Inlines.Add(new Run
                {
                    Text = inline.Text,
                    FontFamily = new FontFamily("Cascadia Mono"),
                });
                break;
            case ReleaseNotesInlineKind.Link when TryCreateSafeUri(inline.Url, out var uri):
                var link = new Hyperlink { NavigateUri = uri };
                link.Inlines.Add(new Run { Text = inline.Text });
                paragraph.Inlines.Add(link);
                break;
            case ReleaseNotesInlineKind.Link:
                paragraph.Inlines.Add(new Run { Text = $"{inline.Text} ({inline.Url})" });
                break;
            default:
                paragraph.Inlines.Add(new Run { Text = inline.Text });
                break;
        }
    }

    private static bool TryCreateSafeUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        uri = null!;
        return false;
    }

    private static Brush? ResourceBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
