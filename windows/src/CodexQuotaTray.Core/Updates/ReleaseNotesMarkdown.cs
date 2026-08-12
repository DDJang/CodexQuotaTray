namespace CodexQuotaTray.Core.Updates;

public enum ReleaseNotesBlockKind
{
    Heading,
    Paragraph,
    UnorderedListItem,
    OrderedListItem,
    Quote,
    CodeBlock,
}

public enum ReleaseNotesInlineKind
{
    Text,
    Bold,
    Italic,
    InlineCode,
    Link,
}

public sealed record ReleaseNotesInline(
    ReleaseNotesInlineKind Kind,
    string Text,
    string? Url = null);

public sealed record ReleaseNotesBlock(
    ReleaseNotesBlockKind Kind,
    int Level,
    IReadOnlyList<ReleaseNotesInline> Inlines,
    int ListIndex = 0);

public static class ReleaseNotesMarkdown
{
    public static IReadOnlyList<ReleaseNotesBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var blocks = new List<ReleaseNotesBlock>();
        var paragraph = new List<string>();
        var code = new List<string>();
        var inCode = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            blocks.Add(new ReleaseNotesBlock(
                ReleaseNotesBlockKind.Paragraph,
                0,
                ParseInline(string.Join(Environment.NewLine, paragraph))));
            paragraph.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();
            if (inCode)
            {
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    blocks.Add(new ReleaseNotesBlock(
                        ReleaseNotesBlockKind.CodeBlock,
                        0,
                        [new ReleaseNotesInline(ReleaseNotesInlineKind.Text, string.Join(Environment.NewLine, code))]));
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    code.Add(line);
                }

                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                inCode = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (TryParseHeading(trimmed, out var headingLevel, out var headingText))
            {
                FlushParagraph();
                blocks.Add(new ReleaseNotesBlock(
                    ReleaseNotesBlockKind.Heading,
                    headingLevel,
                    ParseInline(headingText)));
                continue;
            }

            if (TryParseUnorderedItem(trimmed, out var unorderedText))
            {
                FlushParagraph();
                blocks.Add(new ReleaseNotesBlock(
                    ReleaseNotesBlockKind.UnorderedListItem,
                    0,
                    ParseInline(unorderedText)));
                continue;
            }

            if (TryParseOrderedItem(trimmed, out var listIndex, out var orderedText))
            {
                FlushParagraph();
                blocks.Add(new ReleaseNotesBlock(
                    ReleaseNotesBlockKind.OrderedListItem,
                    0,
                    ParseInline(orderedText),
                    listIndex));
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                FlushParagraph();
                var quote = trimmed[1..].TrimStart();
                blocks.Add(new ReleaseNotesBlock(
                    ReleaseNotesBlockKind.Quote,
                    0,
                    ParseInline(quote)));
                continue;
            }

            paragraph.Add(line);
        }

        if (inCode)
        {
            blocks.Add(new ReleaseNotesBlock(
                ReleaseNotesBlockKind.CodeBlock,
                0,
                [new ReleaseNotesInline(ReleaseNotesInlineKind.Text, string.Join(Environment.NewLine, code))]));
        }

        FlushParagraph();
        return blocks;
    }

    internal static IReadOnlyList<ReleaseNotesInline> ParseInline(string text)
    {
        var result = new List<ReleaseNotesInline>();
        var plainStart = 0;
        var index = 0;

        void FlushPlain(int end)
        {
            if (end > plainStart)
            {
                result.Add(new ReleaseNotesInline(
                    ReleaseNotesInlineKind.Text,
                    text[plainStart..end]));
            }
        }

        while (index < text.Length)
        {
            ReleaseNotesInlineKind kind;
            string? marker;
            if (text.AsSpan(index).StartsWith("**", StringComparison.Ordinal))
            {
                kind = ReleaseNotesInlineKind.Bold;
                marker = "**";
            }
            else if (text[index] == '*')
            {
                kind = ReleaseNotesInlineKind.Italic;
                marker = "*";
            }
            else if (text[index] == '`')
            {
                kind = ReleaseNotesInlineKind.InlineCode;
                marker = "`";
            }
            else if (text[index] == '[')
            {
                var closeLabel = text.IndexOf(']', index + 1);
                if (closeLabel > index
                    && closeLabel + 1 < text.Length
                    && text[closeLabel + 1] == '('
                    && text.IndexOf(')', closeLabel + 2) is var closeUrl
                    && closeUrl > closeLabel + 2)
                {
                    FlushPlain(index);
                    var label = text[(index + 1)..closeLabel];
                    var url = text[(closeLabel + 2)..closeUrl];
                    result.Add(new ReleaseNotesInline(ReleaseNotesInlineKind.Link, label, url));
                    index = closeUrl + 1;
                    plainStart = index;
                    continue;
                }

                index++;
                continue;
            }
            else
            {
                index++;
                continue;
            }

            var contentStart = index + marker.Length;
            var close = text.IndexOf(marker, contentStart, StringComparison.Ordinal);
            if (close <= contentStart)
            {
                index += marker.Length;
                continue;
            }

            FlushPlain(index);
            result.Add(new ReleaseNotesInline(kind, text[contentStart..close]));
            index = close + marker.Length;
            plainStart = index;
        }

        FlushPlain(text.Length);
        return result;
    }

    private static bool TryParseHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        while (level < 3 && level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level == 0 || level >= line.Length || !char.IsWhiteSpace(line[level]))
        {
            level = 0;
            return false;
        }

        text = line[(level + 1)..].Trim();
        return true;
    }

    private static bool TryParseUnorderedItem(string line, out string text)
    {
        text = string.Empty;
        if (line.Length < 3 || line[1] != ' ' || line[0] is not ('-' or '*' or '+'))
        {
            return false;
        }

        text = line[2..].Trim();
        return text.Length > 0;
    }

    private static bool TryParseOrderedItem(string line, out int index, out string text)
    {
        index = 0;
        text = string.Empty;
        var separator = line.IndexOf(". ", StringComparison.Ordinal);
        if (separator <= 0
            || !int.TryParse(line[..separator], out index)
            || index < 1)
        {
            return false;
        }

        text = line[(separator + 2)..].Trim();
        return text.Length > 0;
    }
}
