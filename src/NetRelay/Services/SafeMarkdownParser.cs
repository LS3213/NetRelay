using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;

namespace NetRelay.Services;

public static class SafeMarkdownParser
{
    public static FlowDocument Parse(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei"),
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.Black,
            LineHeight = 20
        };

        var lines = markdown.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        List? currentList = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Handle headers
            if (line.StartsWith("#"))
            {
                currentList = null;
                var level = 0;
                while (level < line.Length && line[level] == '#') level++;
                var headerText = line.Substring(level).Trim();
                
                var p = new Paragraph();
                p.Margin = new Thickness(0, level == 1 ? 14 : 8, 0, 4);
                p.FontWeight = FontWeights.Bold;
                p.FontSize = level == 1 ? 16 : level == 2 ? 14 : 12;
                
                ParseInlines(headerText, p.Inlines);
                doc.Blocks.Add(p);
                continue;
            }

            // Handle bullet lists
            if (line.StartsWith("-") || line.StartsWith("*"))
            {
                var itemText = line.Substring(1).Trim();
                if (currentList == null)
                {
                    currentList = new List();
                    currentList.Margin = new Thickness(15, 0, 0, 10);
                    doc.Blocks.Add(currentList);
                }

                var listItem = new ListItem();
                var p = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                ParseInlines(itemText, p.Inlines);
                listItem.Blocks.Add(p);
                currentList.ListItems.Add(listItem);
                continue;
            }

            // Regular paragraph
            currentList = null;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var para = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            ParseInlines(line, para.Inlines);
            doc.Blocks.Add(para);
        }

        return doc;
    }

    private static void ParseInlines(string text, InlineCollection inlines)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            // Try matching link: [text](url)
            if (text[pos] == '[')
            {
                var endText = text.IndexOf(']', pos);
                if (endText > pos && endText + 1 < text.Length && text[endText + 1] == '(')
                {
                    var endUrl = text.IndexOf(')', endText + 2);
                    if (endUrl > endText)
                    {
                        var linkText = text.Substring(pos + 1, endText - pos - 1);
                        var linkUrl = text.Substring(endText + 2, endUrl - endText - 2).Trim();

                        // Strict Whitelist Protocol Validation
                        var isSafe = linkUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                     linkUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                        if (isSafe)
                        {
                            var hyperlink = new Hyperlink();
                            try
                            {
                                hyperlink.NavigateUri = new Uri(linkUrl);
                                hyperlink.RequestNavigate += (sender, e) =>
                                {
                                    try
                                    {
                                        Process.Start(new ProcessStartInfo
                                        {
                                            FileName = e.Uri.AbsoluteUri,
                                            UseShellExecute = true
                                        });
                                        e.Handled = true;
                                    }
                                    catch { }
                                };
                                ParseBoldAndItalic(linkText, hyperlink.Inlines);
                                inlines.Add(hyperlink);
                            }
                            catch
                            {
                                inlines.Add(new Run($"{linkText} ({linkUrl})") { Foreground = System.Windows.Media.Brushes.Gray });
                            }
                        }
                        else
                        {
                            // Render unsafe link as plain text: [text](unsafe-url)
                            inlines.Add(new Run($"{linkText} ({linkUrl})") { Foreground = System.Windows.Media.Brushes.Gray });
                        }

                        pos = endUrl + 1;
                        continue;
                    }
                }
            }

            // Normal text block up to next link
            var nextSpecial = pos + 1 < text.Length ? text.IndexOf('[', pos + 1) : -1;
            var nextPos = nextSpecial >= 0 ? nextSpecial : text.Length;

            var chunk = text.Substring(pos, nextPos - pos);
            ParseBoldAndItalic(chunk, inlines);
            pos = nextPos;
        }
    }

    private static void ParseBoldAndItalic(string text, InlineCollection inlines)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var boldIndex = text.IndexOf("**", pos);
            if (boldIndex == pos)
            {
                var endBold = text.IndexOf("**", pos + 2);
                if (endBold > pos + 2)
                {
                    var boldText = text.Substring(pos + 2, endBold - pos - 2);
                    inlines.Add(new Run(boldText) { FontWeight = FontWeights.Bold });
                    pos = endBold + 2;
                    continue;
                }
            }

            var nextBold = pos + 1 < text.Length ? text.IndexOf("**", pos + 1) : -1;
            var nextPos = nextBold >= 0 ? nextBold : text.Length;
            var chunk = text.Substring(pos, nextPos - pos);
            if (!string.IsNullOrEmpty(chunk))
            {
                inlines.Add(new Run(chunk));
            }
            pos = nextPos;
        }
    }
}
