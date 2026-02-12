using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;

namespace ResearchHive.Controls;

/// <summary>
/// A control that renders Markdown text as a WPF FlowDocument.
/// Uses Markdig to parse the Markdown AST and converts it to WPF document elements.
/// </summary>
public class MarkdownViewer : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(
            nameof(Markdown),
            typeof(string),
            typeof(MarkdownViewer),
            new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public MarkdownViewer()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        IsToolBarVisible = false;
        Document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            PagePadding = new Thickness(16),
            LineStackingStrategy = LineStackingStrategy.MaxHeight
        };
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer viewer)
        {
            viewer.RenderMarkdown((string)e.NewValue ?? "");
        }
    }

    private void RenderMarkdown(string markdown)
    {
        if (Document == null)
            Document = new FlowDocument();

        Document.Blocks.Clear();

        if (string.IsNullOrWhiteSpace(markdown))
            return;

        try
        {
            var doc = Markdig.Markdown.Parse(markdown, Pipeline);
            foreach (var block in doc)
            {
                var wpfBlock = ConvertBlock(block);
                if (wpfBlock != null)
                    Document.Blocks.Add(wpfBlock);
            }
        }
        catch
        {
            // Fallback: render as plain text
            Document.Blocks.Add(new Paragraph(new Run(markdown)));
        }
    }

    private WpfBlock? ConvertBlock(Markdig.Syntax.Block block)
    {
        return block switch
        {
            HeadingBlock heading => ConvertHeading(heading),
            ParagraphBlock paragraph => ConvertParagraph(paragraph),
            ListBlock list => ConvertList(list),
            Markdig.Extensions.Tables.Table table => ConvertTable(table),
            FencedCodeBlock fencedCode => ConvertCodeBlock(fencedCode),
            CodeBlock code => ConvertCodeBlock(code),
            QuoteBlock quote => ConvertQuote(quote),
            ThematicBreakBlock => ConvertThematicBreak(),
            _ => ConvertGenericBlock(block)
        };
    }

    private Paragraph ConvertHeading(HeadingBlock heading)
    {
        var paragraph = new Paragraph
        {
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1976D2")),
            Margin = new Thickness(0, heading.Level <= 2 ? 16 : 8, 0, 6)
        };

        paragraph.FontSize = heading.Level switch
        {
            1 => 24,
            2 => 20,
            3 => 16,
            4 => 14,
            _ => 13
        };

        if (heading.Inline != null)
        {
            foreach (var inline in heading.Inline)
                paragraph.Inlines.Add(ConvertInline(inline));
        }

        return paragraph;
    }

    private Paragraph ConvertParagraph(ParagraphBlock paragraph)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };

        if (paragraph.Inline != null)
        {
            foreach (var inline in paragraph.Inline)
                p.Inlines.Add(ConvertInline(inline));
        }

        return p;
    }

    private List ConvertList(ListBlock listBlock)
    {
        var list = new List
        {
            Margin = new Thickness(0, 4, 0, 8),
            MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc
        };

        foreach (var item in listBlock)
        {
            if (item is ListItemBlock listItem)
            {
                var listItemElement = new ListItem();
                foreach (var sub in listItem)
                {
                    var wpfBlock = ConvertBlock(sub);
                    if (wpfBlock != null)
                        listItemElement.Blocks.Add(wpfBlock);
                }
                list.ListItems.Add(listItemElement);
            }
        }

        return list;
    }

    private WpfBlock ConvertTable(Markdig.Extensions.Tables.Table table)
    {
        var wpfTable = new System.Windows.Documents.Table
        {
            CellSpacing = 0,
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0")),
            BorderThickness = new Thickness(1)
        };

        // Determine column count from first row
        int colCount = 0;
        foreach (var row in table)
        {
            if (row is Markdig.Extensions.Tables.TableRow tr)
            {
                colCount = Math.Max(colCount, tr.Count);
            }
        }

        for (int i = 0; i < colCount; i++)
            wpfTable.Columns.Add(new TableColumn());

        var rowGroup = new TableRowGroup();
        var headerBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F5F5"));
        var borderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0"));

        foreach (var row in table)
        {
            if (row is not Markdig.Extensions.Tables.TableRow tableRow) continue;
            var wpfRow = new System.Windows.Documents.TableRow();

            if (tableRow.IsHeader)
                wpfRow.Background = headerBg;

            foreach (var cell in tableRow)
            {
                if (cell is not Markdig.Extensions.Tables.TableCell tableCell) continue;
                var wpfCell = new System.Windows.Documents.TableCell
                {
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4)
                };

                foreach (var sub in tableCell)
                {
                    var wpfBlock = ConvertBlock(sub);
                    if (wpfBlock != null)
                    {
                        // Remove paragraph margins inside cells for compact layout
                        if (wpfBlock is Paragraph p)
                            p.Margin = new Thickness(0);
                        wpfCell.Blocks.Add(wpfBlock);
                    }
                }

                if (tableCell.ColumnSpan > 1)
                    wpfCell.ColumnSpan = tableCell.ColumnSpan;
                if (tableCell.RowSpan > 1)
                    wpfCell.RowSpan = tableCell.RowSpan;

                if (tableRow.IsHeader)
                    wpfCell.Blocks.OfType<Paragraph>().ToList().ForEach(p => p.FontWeight = FontWeights.SemiBold);

                wpfRow.Cells.Add(wpfCell);
            }

            rowGroup.Rows.Add(wpfRow);
        }

        wpfTable.RowGroups.Add(rowGroup);
        return wpfTable;
    }

    private Section ConvertCodeBlock(CodeBlock codeBlock)
    {
        var text = codeBlock.Lines.ToString();
        var section = new Section
        {
            Margin = new Thickness(0, 4, 0, 8)
        };

        var paragraph = new Paragraph(new Run(text))
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F5F5")),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#212121")),
            Padding = new Thickness(12, 8, 12, 8),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0")),
            BorderThickness = new Thickness(1)
        };

        section.Blocks.Add(paragraph);
        return section;
    }

    private Section ConvertQuote(QuoteBlock quote)
    {
        var section = new Section
        {
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(12, 4, 4, 4),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1976D2")),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F9FF"))
        };

        foreach (var sub in quote)
        {
            var wpfBlock = ConvertBlock(sub);
            if (wpfBlock != null)
                section.Blocks.Add(wpfBlock);
        }

        return section;
    }

    private Paragraph ConvertThematicBreak()
    {
        return new Paragraph
        {
            Margin = new Thickness(0, 8, 0, 8),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            FontSize = 1
        };
    }

    private WpfBlock? ConvertGenericBlock(Markdig.Syntax.Block block)
    {
        // For unknown block types, try to extract text
        if (block is LeafBlock leaf && leaf.Inline != null)
        {
            var p = new Paragraph();
            foreach (var inline in leaf.Inline)
                p.Inlines.Add(ConvertInline(inline));
            return p;
        }

        if (block is ContainerBlock container)
        {
            var section = new Section();
            foreach (var child in container)
            {
                var wpfBlock = ConvertBlock(child);
                if (wpfBlock != null)
                    section.Blocks.Add(wpfBlock);
            }
            return section;
        }

        return null;
    }

    private WpfInline ConvertInline(Markdig.Syntax.Inlines.Inline inline)
    {
        return inline switch
        {
            LiteralInline literal => new Run(literal.Content.ToString()),
            EmphasisInline emphasis => ConvertEmphasis(emphasis),
            CodeInline code => ConvertCodeInline(code),
            LinkInline link => ConvertLink(link),
            LineBreakInline => new LineBreak(),
            HtmlInline html => new Run(html.Tag),
            // Delimiter inlines (e.g. LinkDelimiterInline for "[") are internal
            // Markdig bookkeeping. Render as empty to avoid class-name leak.
            DelimiterInline => new Run(),
            // AutolinkInline renders its URL text
            AutolinkInline autolink => new Run(autolink.Url),
            _ => new Run(inline.ToString() ?? "")
        };
    }

    private WpfInline ConvertEmphasis(EmphasisInline emphasis)
    {
        var span = new Span();

        if (emphasis.DelimiterCount >= 2)
            span.FontWeight = FontWeights.Bold;
        else
            span.FontStyle = FontStyles.Italic;

        foreach (var child in emphasis)
            span.Inlines.Add(ConvertInline(child));

        return span;
    }

    private WpfInline ConvertCodeInline(CodeInline code)
    {
        return new Run(code.Content)
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0F0F0")),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C62828"))
        };
    }

    private WpfInline ConvertLink(LinkInline link)
    {
        var hyperlink = new Hyperlink
        {
            NavigateUri = Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) ? uri : null,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1976D2"))
        };

        if (link.Any())
        {
            foreach (var child in link)
                hyperlink.Inlines.Add(ConvertInline(child));
        }
        else
        {
            hyperlink.Inlines.Add(new Run(link.Url ?? link.Title ?? "link"));
        }

        hyperlink.RequestNavigate += (_, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch { /* ignore navigation failures */ }
        };

        return hyperlink;
    }
}
