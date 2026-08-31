using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Timenotifier.Views.Assistant;

internal static class AssistantMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    public static FlowDocument Render(string markdown)
    {
        var displayMarkdown = AssistantOutputFormatter.NormalizeMarkdown(markdown);
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 13.5,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 32, 51)),
            LineHeight = 22
        };

        var parsed = Markdown.Parse(displayMarkdown, Pipeline);
        foreach (var block in parsed)
        {
            AddBlock(document.Blocks, block);
        }

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run(displayMarkdown)) { Margin = new Thickness(0) });
        }
        return document;
    }

    private static void AddBlock(BlockCollection target, Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var headingParagraph = CreateParagraph(heading.Inline);
                headingParagraph.FontWeight = FontWeights.SemiBold;
                headingParagraph.FontSize = heading.Level <= 2 ? 17 : 15;
                headingParagraph.Margin = new Thickness(0, 6, 0, 5);
                target.Add(headingParagraph);
                break;
            case ParagraphBlock paragraph:
                var body = CreateParagraph(paragraph.Inline);
                body.Margin = new Thickness(0, 0, 0, 7);
                target.Add(body);
                break;
            case Markdig.Syntax.ListBlock listBlock:
                var list = new System.Windows.Documents.List
                {
                    MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(18, 0, 0, 7),
                    Padding = new Thickness(0)
                };
                foreach (var child in listBlock.OfType<ListItemBlock>())
                {
                    var item = new ListItem { Margin = new Thickness(0, 0, 0, 3) };
                    foreach (var childBlock in child)
                    {
                        AddBlock(item.Blocks, childBlock);
                    }
                    list.ListItems.Add(item);
                }
                target.Add(list);
                break;
            case FencedCodeBlock fenced:
                AddCodeBlock(target, fenced.Lines.ToString());
                break;
            case CodeBlock code:
                AddCodeBlock(target, code.Lines.ToString());
                break;
        }
    }

    private static void AddCodeBlock(BlockCollection target, string code)
    {
        target.Add(new Paragraph(new Run(code.TrimEnd()))
        {
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12.5,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 243, 248)),
            Margin = new Thickness(0, 2, 0, 8),
            Padding = new Thickness(8, 6, 8, 6)
        });
    }

    private static Paragraph CreateParagraph(ContainerInline? container)
    {
        var paragraph = new Paragraph();
        if (container is not null)
        {
            AppendInlines(paragraph.Inlines, container.FirstChild);
        }
        return paragraph;
    }

    private static void AppendInlines(InlineCollection target, Markdig.Syntax.Inlines.Inline? inline)
    {
        for (var current = inline; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 243, 248))
                    });
                    break;
                case EmphasisInline emphasis:
                    var span = new Span
                    {
                        FontWeight = emphasis.DelimiterCount >= 2 ? FontWeights.SemiBold : FontWeights.Normal,
                        FontStyle = emphasis.DelimiterCount == 1 ? FontStyles.Italic : FontStyles.Normal
                    };
                    AppendInlines(span.Inlines, emphasis.FirstChild);
                    target.Add(span);
                    break;
                case LinkInline link:
                    var linkText = new Span();
                    AppendInlines(linkText.Inlines, link.FirstChild);
                    target.Add(linkText);
                    break;
                case ContainerInline container:
                    AppendInlines(target, container.FirstChild);
                    break;
            }
        }
    }
}
