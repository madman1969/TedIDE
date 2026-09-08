using System.Text;
using HtmlAgilityPack;

namespace Tedide.DocViewer;

/// <summary>
/// Reduces a cc65 manual page's HTML (LinuxDoc-Tools output - a small, consistent set of tags; see
/// <see cref="Cc65DocCatalog"/>) to plain text for display in a Terminal.Gui <c>TextView</c>.
/// Headings get an underline, paragraphs are word-wrapped to <see cref="WrapWidth"/>, and
/// &lt;PRE&gt; blocks (command examples, code listings) are kept verbatim so their alignment
/// survives - everything else is simplified rather than faithfully reproduced (hyperlinks lose
/// their target, tables become one line per row).
/// </summary>
public static class DocTextConverter
{
    private const int WrapWidth = 78;

    public static string ToPlainText(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var writer = new Writer();
        AppendChildren(document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode, writer);

        // Collapses the runs of blank lines that block elements' leading/trailing EnsureBlankLine
        // calls leave between each other down to exactly one.
        var text = writer.ToString();
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
            text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        return text.Trim('\n') + "\n";
    }

    /// <summary>Wraps a <see cref="StringBuilder"/> with a running count of the current (last)
    /// line's length, so <see cref="AppendBlockText"/> can word-wrap without re-scanning the whole
    /// buffer for its last newline on every text node - the naive approach, for a ~380KB manual
    /// page split across many small text nodes, made conversion visibly slow.</summary>
    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();
        public int CurrentLineLength { get; private set; }

        public void Append(string s)
        {
            _sb.Append(s);
            var lastNewline = s.LastIndexOf('\n');
            CurrentLineLength = lastNewline >= 0 ? s.Length - lastNewline - 1 : CurrentLineLength + s.Length;
        }

        public void Append(char c) => Append(c.ToString());

        public int Length => _sb.Length;
        public char this[int index] => _sb[index];

        public override string ToString() => _sb.ToString();
    }

    private static void AppendChildren(HtmlNode node, Writer w)
    {
        foreach (var child in node.ChildNodes)
            AppendNode(child, w);
    }

    private static void AppendNode(HtmlNode node, Writer w)
    {
        if (node.NodeType == HtmlNodeType.Comment)
            return;

        if (node.NodeType == HtmlNodeType.Text)
        {
            AppendBlockText(w, CollapseWhitespace(HtmlEntity.DeEntitize(node.InnerText)));
            return;
        }

        switch (node.Name.ToLowerInvariant())
        {
            case "head":
            case "title":
            case "script":
            case "style":
                return;

            case "h1":
                AppendHeading(w, node, '=');
                return;
            case "h2":
            case "h3":
                AppendHeading(w, node, '-');
                return;

            case "p":
            case "center":
            case "blockquote":
            case "div":
                EnsureBlankLine(w);
                AppendChildren(node, w);
                EnsureBlankLine(w);
                return;

            case "br":
                w.Append('\n');
                return;

            case "hr":
                EnsureBlankLine(w);
                w.Append(new string('-', WrapWidth));
                w.Append('\n');
                EnsureBlankLine(w);
                return;

            case "pre":
                EnsureBlankLine(w);
                foreach (var line in HtmlEntity.DeEntitize(node.InnerText).TrimEnd('\n', '\r').Split('\n'))
                {
                    w.Append("    ");
                    w.Append(line.TrimEnd('\r'));
                    w.Append('\n');
                }
                EnsureBlankLine(w);
                return;

            case "ul":
            case "ol":
                EnsureBlankLine(w);
                AppendListItems(node, w, ordered: string.Equals(node.Name, "ol", StringComparison.OrdinalIgnoreCase));
                EnsureBlankLine(w);
                return;

            case "dl":
                EnsureBlankLine(w);
                AppendDefinitionList(node, w);
                EnsureBlankLine(w);
                return;

            case "table":
                EnsureBlankLine(w);
                AppendTable(node, w);
                EnsureBlankLine(w);
                return;

            default:
                // Inline elements (a, b, em, code, tt, ...) and anything else unrecognized: just
                // keep their text content flowing into the surrounding paragraph.
                AppendChildren(node, w);
                return;
        }
    }

    private static void AppendHeading(Writer w, HtmlNode node, char underline)
    {
        var text = CollapseWhitespace(HtmlEntity.DeEntitize(node.InnerText)).Trim();
        if (text.Length == 0)
            return;

        EnsureBlankLine(w);
        w.Append(text);
        w.Append('\n');
        w.Append(new string(underline, text.Length));
        w.Append('\n');
        EnsureBlankLine(w);
    }

    private static void AppendListItems(HtmlNode list, Writer w, bool ordered)
    {
        var index = 1;
        foreach (var item in list.ChildNodes.Where(n => string.Equals(n.Name, "li", StringComparison.OrdinalIgnoreCase)))
        {
            var marker = ordered ? $"{index++}. " : "- ";
            var continuation = new string(' ', marker.Length);
            var text = CollapseWhitespace(HtmlEntity.DeEntitize(item.InnerText)).Trim();

            var isFirstLine = true;
            foreach (var line in WrapText(text, WrapWidth - marker.Length))
            {
                w.Append(isFirstLine ? marker : continuation);
                w.Append(line);
                w.Append('\n');
                isFirstLine = false;
            }
        }
    }

    private static void AppendDefinitionList(HtmlNode dl, Writer w)
    {
        foreach (var child in dl.ChildNodes)
        {
            var text = CollapseWhitespace(HtmlEntity.DeEntitize(child.InnerText)).Trim();
            if (text.Length == 0)
                continue;

            if (string.Equals(child.Name, "dt", StringComparison.OrdinalIgnoreCase))
            {
                w.Append(text);
                w.Append('\n');
            }
            else if (string.Equals(child.Name, "dd", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in WrapText(text, WrapWidth - 4))
                {
                    w.Append("    ");
                    w.Append(line);
                    w.Append('\n');
                }
                w.Append('\n');
            }
        }
    }

    private static void AppendTable(HtmlNode table, Writer w)
    {
        foreach (var row in table.Descendants("tr"))
        {
            var cells = row.ChildNodes
                .Where(n => n.Name is "td" or "th")
                .Select(n => CollapseWhitespace(HtmlEntity.DeEntitize(n.InnerText)).Trim());
            w.Append(string.Join("  |  ", cells));
            w.Append('\n');
        }
    }

    /// <summary>Appends already-collapsed text into the current paragraph, word-wrapping the
    /// running line at <see cref="WrapWidth"/>. Whitespace-only text (e.g. the indentation between
    /// two &lt;LI&gt;s) collapses to "" upstream and is skipped here, so it can't force a spurious
    /// line break.</summary>
    private static void AppendBlockText(Writer w, string text)
    {
        if (text.Length == 0)
            return;

        var separator = w.CurrentLineLength > 0 && !text.StartsWith(' ') ? " " : "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w.CurrentLineLength > 0 && w.CurrentLineLength + separator.Length + word.Length > WrapWidth)
            {
                w.Append('\n');
                separator = "";
            }
            w.Append(separator);
            w.Append(word);
            separator = " ";
        }
    }

    private static IEnumerable<string> WrapText(string text, int width)
    {
        if (width < 10)
            width = 10;

        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0)
            yield return line.ToString();
    }

    private static void EnsureBlankLine(Writer w)
    {
        if (w.Length == 0)
            return;
        if (w[w.Length - 1] != '\n')
            w.Append('\n');
        if (w.Length < 2 || w[w.Length - 2] != '\n')
            w.Append('\n');
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
