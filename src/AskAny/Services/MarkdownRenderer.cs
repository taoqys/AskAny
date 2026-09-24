using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AskAny.Services;

public static partial class MarkdownRenderer
{
    private static readonly Brush InkBrush = new SolidColorBrush(Color.FromRgb(32, 33, 36));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(108, 109, 114));
    private static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(220, 221, 225));
    private static readonly Brush CodeBrush = new SolidColorBrush(Color.FromRgb(242, 242, 245));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(74, 99, 190));

    public static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            Foreground = InkBrush,
            PagePadding = new Thickness(0),
            LineHeight = 23
        };

        var lines = (markdown ?? string.Empty).ReplaceLineEndings("\n").Split('\n');
        var paragraphLines = new List<string>();
        var codeLines = new List<string>();
        var activeList = new List();
        var listType = ListKind.None;
        var inCodeBlock = false;

        void FlushParagraph()
        {
            if (paragraphLines.Count == 0)
            {
                return;
            }

            var paragraph = CreateParagraph();
            AddInline(paragraph, string.Join(" ", paragraphLines).Trim());
            document.Blocks.Add(paragraph);
            paragraphLines.Clear();
        }

        void FlushList()
        {
            if (activeList.ListItems.Count > 0)
            {
                document.Blocks.Add(activeList);
            }

            activeList = new List
            {
                MarkerStyle = TextMarkerStyle.Disc,
                Margin = new Thickness(18, 3, 0, 8),
                Padding = new Thickness(0)
            };
            listType = ListKind.None;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var rawLine = lines[index];
            var line = rawLine.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();
                if (inCodeBlock)
                {
                    var codeParagraph = new Paragraph(new Run(string.Join(Environment.NewLine, codeLines)))
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 12.5,
                        Background = CodeBrush,
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 5, 0, 10),
                        BorderBrush = LineBrush,
                        BorderThickness = new Thickness(1)
                    };
                    document.Blocks.Add(codeParagraph);
                    codeLines.Clear();
                }

                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock)
            {
                codeLines.Add(rawLine);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            if (IsTableStart(lines, index))
            {
                FlushParagraph();
                FlushList();

                var headers = SplitTableRow(line);
                var alignments = SplitTableRow(lines[index + 1])
                    .Select(ParseTableAlignment)
                    .ToArray();
                var tableRows = new List<List<string>>();
                var rowIndex = index + 2;
                while (rowIndex < lines.Length &&
                       !string.IsNullOrWhiteSpace(lines[rowIndex]) &&
                       lines[rowIndex].Contains('|'))
                {
                    tableRows.Add(SplitTableRow(lines[rowIndex]));
                    rowIndex++;
                }

                document.Blocks.Add(CreateTable(headers, alignments, tableRows));
                index = rowIndex - 1;
                continue;
            }

            if (HeadingRegex().IsMatch(line))
            {
                FlushParagraph();
                FlushList();
                var match = HeadingRegex().Match(line);
                var level = match.Groups[1].Value.Length;
                var paragraph = CreateParagraph();
                paragraph.FontSize = level switch
                {
                    1 => 24,
                    2 => 20,
                    3 => 17,
                    _ => 15
                };
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, level <= 2 ? 9 : 6, 0, 7);
                AddInline(paragraph, match.Groups[2].Value.Trim());
                document.Blocks.Add(paragraph);
                continue;
            }

            if (HorizontalRuleRegex().IsMatch(line))
            {
                FlushParagraph();
                FlushList();
                document.Blocks.Add(new Paragraph
                {
                    BorderBrush = LineBrush,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 10, 0, 10)
                });
                continue;
            }

            var bulletMatch = BulletRegex().Match(line);
            if (bulletMatch.Success)
            {
                FlushParagraph();
                if (listType != ListKind.Bullet)
                {
                    FlushList();
                    listType = ListKind.Bullet;
                    activeList.MarkerStyle = TextMarkerStyle.Disc;
                }

                var itemParagraph = CreateParagraph();
                AddInline(itemParagraph, bulletMatch.Groups[1].Value.Trim());
                activeList.ListItems.Add(new ListItem(itemParagraph));
                continue;
            }

            var numberedMatch = NumberedRegex().Match(line);
            if (numberedMatch.Success)
            {
                FlushParagraph();
                if (listType != ListKind.Numbered)
                {
                    FlushList();
                    listType = ListKind.Numbered;
                    activeList.MarkerStyle = TextMarkerStyle.Decimal;
                }

                var itemParagraph = CreateParagraph();
                AddInline(itemParagraph, numberedMatch.Groups[1].Value.Trim());
                activeList.ListItems.Add(new ListItem(itemParagraph));
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                FlushParagraph();
                FlushList();
                var paragraph = CreateParagraph();
                paragraph.BorderBrush = AccentBrush;
                paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                paragraph.Padding = new Thickness(10, 2, 0, 2);
                paragraph.Foreground = MutedBrush;
                AddInline(paragraph, line.TrimStart()[1..].Trim());
                document.Blocks.Add(paragraph);
                continue;
            }

            paragraphLines.Add(line.Trim());
        }

        FlushParagraph();
        FlushList();

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(CreateParagraph(string.Empty));
        }

        return document;
    }

    private static Table CreateTable(
        List<string> headers,
        TextAlignment[] alignments,
        List<List<string>> rows)
    {
        var columnCount = Math.Max(headers.Count, rows.Count == 0
            ? 0
            : rows.Max(row => row.Count));
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 6, 0, 10),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1)
        };

        for (var column = 0; column < columnCount; column++)
        {
            table.Columns.Add(new TableColumn
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        }

        var group = new TableRowGroup();
        group.Rows.Add(CreateTableRow(headers, alignments, columnCount, true));
        foreach (var row in rows)
        {
            group.Rows.Add(CreateTableRow(row, alignments, columnCount, false));
        }

        table.RowGroups.Add(group);
        return table;
    }

    private static TableRow CreateTableRow(
        IReadOnlyList<string> values,
        IReadOnlyList<TextAlignment> alignments,
        int columnCount,
        bool isHeader)
    {
        var row = new TableRow
        {
            Background = isHeader ? CodeBrush : Brushes.Transparent
        };

        for (var column = 0; column < columnCount; column++)
        {
            var paragraph = CreateParagraph();
            paragraph.Margin = new Thickness(0);
            paragraph.TextAlignment = column < alignments.Count
                ? alignments[column]
                : TextAlignment.Left;
            if (isHeader)
            {
                paragraph.FontWeight = FontWeights.SemiBold;
            }

            AddInline(
                paragraph,
                column < values.Count ? values[column].Trim() : string.Empty);
            row.Cells.Add(new TableCell(paragraph)
            {
                Padding = new Thickness(8, 5, 8, 5),
                BorderBrush = LineBrush,
                BorderThickness = new Thickness(0, 0, 1, 1)
            });
        }

        return row;
    }

    private static bool IsTableStart(string[] lines, int index)
    {
        return index + 1 < lines.Length &&
               lines[index].Contains('|') &&
               IsTableSeparator(lines[index + 1]);
    }

    private static bool IsTableSeparator(string line)
    {
        if (!line.Contains('|'))
        {
            return false;
        }

        var cells = SplitTableRow(line);
        return cells.Count > 0 &&
               cells.All(cell => TableSeparatorRegex().IsMatch(cell.Trim()));
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed
            .Split('|')
            .Select(cell => cell.Trim())
            .ToList();
    }

    private static TextAlignment ParseTableAlignment(string separator)
    {
        var value = separator.Trim();
        var left = value.StartsWith(':');
        var right = value.EndsWith(':');
        return (left, right) switch
        {
            (true, true) => TextAlignment.Center,
            (false, true) => TextAlignment.Right,
            _ => TextAlignment.Left
        };
    }

    private static Paragraph CreateParagraph(string? text = null)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 3, 0, 7)
        };

        if (!string.IsNullOrEmpty(text))
        {
            paragraph.Inlines.Add(new Run(text));
        }

        return paragraph;
    }

    private static void AddInline(Paragraph paragraph, string text)
    {
        var position = 0;
        foreach (Match match in InlineRegex().Matches(text))
        {
            if (match.Index > position)
            {
                paragraph.Inlines.Add(new Run(text[position..match.Index]));
            }

            var token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal) && token.EndsWith("**", StringComparison.Ordinal))
            {
                paragraph.Inlines.Add(new Run(token[2..^2]) { FontWeight = FontWeights.SemiBold });
            }
            else if (token.StartsWith('`') && token.EndsWith('`'))
            {
                paragraph.Inlines.Add(new Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Background = CodeBrush
                });
            }
            else if (token.StartsWith('[') && LinkRegex().IsMatch(token))
            {
                var linkMatch = LinkRegex().Match(token);
                var hyperlink = new Hyperlink(new Run(linkMatch.Groups[1].Value))
                {
                    Foreground = AccentBrush,
                    TextDecorations = TextDecorations.Underline
                };
                if (Uri.TryCreate(linkMatch.Groups[2].Value, UriKind.Absolute, out var uri))
                {
                    hyperlink.NavigateUri = uri;
                    hyperlink.RequestNavigate += (_, args) =>
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                        }
                        catch (InvalidOperationException)
                        {
                            // Ignore malformed or blocked links.
                        }
                    };
                }

                paragraph.Inlines.Add(hyperlink);
            }
            else if (token.StartsWith('*') && token.EndsWith('*'))
            {
                paragraph.Inlines.Add(new Run(token[1..^1]) { FontStyle = FontStyles.Italic });
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            paragraph.Inlines.Add(new Run(text[position..]));
        }
    }

    [GeneratedRegex(@"^(#{1,4})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*([-*+])\s+(.+)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\s*\d+\.\s+(.+)$")]
    private static partial Regex NumberedRegex();

    [GeneratedRegex(@"^\s*([-*_])(?:\s*\1){2,}\s*$")]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\([^)]+\)|\*[^*]+\*)")]
    private static partial Regex InlineRegex();

    [GeneratedRegex(@"^\[([^\]]+)\]\(([^)]+)\)$")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^:?-{3,}:?$")]
    private static partial Regex TableSeparatorRegex();

    private enum ListKind
    {
        None,
        Bullet,
        Numbered
    }
}
