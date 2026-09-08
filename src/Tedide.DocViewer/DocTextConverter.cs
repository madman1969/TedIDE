using System.Text;
using HtmlAgilityPack;

namespace Tedide.DocViewer;

/// <summary>One <c>&lt;A HREF&gt;</c> resolved to a location <see cref="DocViewerShell"/> can jump
/// to: <see cref="Row"/>/<see cref="Column"/>/<see cref="Length"/> locate the link's own rendered
/// text (for highlighting and for detecting "the caret is on a link"), while
/// <see cref="TargetFileName"/>/<see cref="TargetAnchor"/> say where it points - always a bundled
/// page (see <see cref="DocTextConverter.Convert"/>'s knownFileNames parameter), optionally a
/// specific heading in it.</summary>
public sealed record DocLink(int Row, int Column, int Length, string TargetFileName, string? TargetAnchor);

/// <summary>How a <see cref="DocSpan"/> or <see cref="DocBlockSpan"/> should be drawn - see
/// <see cref="DocTextView"/>, which maps each to a <c>Terminal.Gui.Drawing.TextStyle</c> layered on
/// top of whatever color the active theme already uses, rather than a new color of its own: that
/// keeps this purely presentational distinction working consistently across every theme without
/// touching <c>Tedide.Theming</c>'s shared (and Tedide.App-visible) scheme definitions.</summary>
public enum DocSpanKind
{
    /// <summary><c>&lt;B&gt;</c>/<c>&lt;STRONG&gt;</c> and heading text.</summary>
    Bold,

    /// <summary><c>&lt;EM&gt;</c>/<c>&lt;I&gt;</c>.</summary>
    Emphasis,

    /// <summary><c>&lt;CODE&gt;</c>/<c>&lt;TT&gt;</c> inline, and <c>&lt;PRE&gt;</c> blocks (as a
    /// <see cref="DocBlockSpan"/> covering every column of the block's rows, not a <see cref="DocSpan"/>).</summary>
    Code,
}

/// <summary>A single-row styled run (bold/emphasis/inline code) - see <see cref="DocSpanKind"/>.
/// Unlike <see cref="DocLink"/> this carries no navigation target, just presentation.</summary>
public sealed record DocSpan(int Row, int Column, int Length, DocSpanKind Kind);

/// <summary>A styled run spanning every column of rows <see cref="StartRow"/>..<see cref="EndRow"/>
/// inclusive - used for <c>&lt;PRE&gt;</c> blocks, which (unlike an inline <see cref="DocSpan"/>) have
/// no single row/column/length to anchor a precise span to.</summary>
public sealed record DocBlockSpan(int StartRow, int EndRow, DocSpanKind Kind);

/// <summary>The result of converting one manual page: its plain text, the internal hyperlinks found
/// in it (<see cref="Links"/>), every <c>&lt;A NAME&gt;</c> anchor's row (<see cref="AnchorRows"/>)
/// so a link targeting this page can jump straight to the right heading, and the presentational
/// spans (<see cref="Spans"/>, <see cref="BlockSpans"/>) that keep bold/emphasis/code text visually
/// distinct from plain prose.</summary>
public sealed record DocConversionResult(
    string Text,
    IReadOnlyList<DocLink> Links,
    IReadOnlyDictionary<string, int> AnchorRows,
    IReadOnlyList<DocSpan> Spans,
    IReadOnlyList<DocBlockSpan> BlockSpans);

/// <summary>
/// Reduces a cc65 manual page's HTML (LinuxDoc-Tools output - a small, consistent set of tags; see
/// <see cref="Cc65DocCatalog"/>) to plain text for display in a Terminal.Gui <c>TextView</c>.
/// Headings get an underline, paragraphs/list items/definitions are word-wrapped to
/// <see cref="WrapWidth"/> (list items and definitions get their continuation lines indented under
/// their marker - see <see cref="Context.Indent"/>), and &lt;PRE&gt; blocks (command examples, code
/// listings) are kept verbatim so their alignment survives.
///
/// Internal hyperlinks (same-page <c>#anchor</c>, or cross-page <c>page.html</c>/<c>page.html#anchor</c>
/// - these manuals never use bare same-page anchors for cross-page links, always
/// <c>currentpage.html#anchor</c>, even for a link to a heading on the very page it's on) are kept as
/// <see cref="DocLink"/>s alongside the text; bold/emphasis/inline code and code blocks are kept as
/// <see cref="DocSpan"/>/<see cref="DocBlockSpan"/>s (see <see cref="DocSpanKind"/>) so the display
/// isn't uniformly flat plain text. Everything else (external http(s) links, table cell content) is
/// simplified rather than faithfully reproduced - table cells lose any links/styling they contain,
/// since flattening a whole row's cells to one line has no sensible place to put a hanging indent for
/// a wrapped link anyway.
/// </summary>
public static class DocTextConverter
{
    private const int WrapWidth = 78;

    /// <param name="html">The page's raw HTML (see <see cref="Cc65DocLoader"/>).</param>
    /// <param name="currentFileName">This page's own <see cref="Cc65DocEntry.FileName"/> - a bare
    /// <c>#anchor</c> href, or one written as <c>currentFileName.html#anchor</c>, both resolve to a
    /// same-page <see cref="DocLink"/> against this.</param>
    /// <param name="knownFileNames">Every bundled page's <see cref="Cc65DocEntry.FileName"/> - an
    /// href to anything outside this set (a page cc65's docs don't ship, or an external URL) is left
    /// as plain text rather than a dead link.</param>
    public static DocConversionResult Convert(string html, string currentFileName, IReadOnlySet<string> knownFileNames)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var context = new Context(new Writer(), currentFileName, knownFileNames);
        AppendChildren(document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode, context);

        // Collapses the runs of blank lines that block elements' leading/trailing EnsureBlankLine
        // calls leave between each other down to exactly one. Links/anchors were already recorded
        // against the pre-collapse row numbers, so do this via a single top-to-bottom rewrite that
        // renumbers rows as it goes, rather than string.Replace (which would silently desync them).
        return CollapseBlankLines(context);
    }

    /// <summary>Bundles the handful of things every Append* method needs, so adding a new one
    /// (like <see cref="Links"/>/<see cref="AnchorRows"/> for this feature) doesn't mean threading
    /// another parameter through every method signature in the file.</summary>
    private sealed class Context(Writer writer, string currentFileName, IReadOnlySet<string> knownFileNames)
    {
        public readonly Writer Writer = writer;
        public readonly string CurrentFileName = currentFileName;
        public readonly IReadOnlySet<string> KnownFileNames = knownFileNames;
        public readonly List<DocLink> Links = [];
        public readonly Dictionary<string, int> AnchorRows = [];
        public readonly List<DocSpan> Spans = [];
        public readonly List<DocBlockSpan> BlockSpans = [];

        /// <summary>Prepended after any line-wrap <see cref="AppendBlockText"/> inserts, so a
        /// wrapped list item or definition's continuation lines land under its marker instead of at
        /// column 0. Empty at the top level and around block elements that don't hang-indent (p,
        /// headings, ...). Not a stack - a list/definition nested inside another briefly overwrites
        /// the outer one's indent for its own extent, which very occasionally under-indents a line
        /// immediately after a nested list back at the outer level; none of the bundled manuals
        /// nest lists deeply enough for this to matter in practice.</summary>
        public string Indent = "";

        /// <summary>Set when the most recently visited text node ended in whitespace, or was
        /// whitespace-only (e.g. the literal single space between two adjacent <c>&lt;A&gt;</c>
        /// elements, or the indentation between sibling tags) - carries "a separating space belongs
        /// here" forward to whatever real text comes next, possibly several AppendNode calls later
        /// (through an anchor tag's own start-of-span bookkeeping, say). Without this, a
        /// whitespace-only text node's only trace - it collapses to "" and is otherwise skipped
        /// entirely - would be lost, and adjacent inline elements with genuine source whitespace
        /// between them would run together with no space at all.</summary>
        public bool PendingSpace;
    }

    /// <summary>Wraps a <see cref="StringBuilder"/> with a running (row, column) position, so
    /// <see cref="AppendBlockText"/> can word-wrap - and <c>&lt;A&gt;</c> handling can record a
    /// link's/anchor's location - without re-scanning the whole buffer on every text node. The
    /// naive re-scan approach, for a ~380KB manual page split across many small text nodes, made
    /// conversion visibly slow.</summary>
    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();
        public int CurrentLineLength { get; private set; }
        public int Row { get; private set; }

        public void Append(string s)
        {
            _sb.Append(s);
            var lastNewline = s.LastIndexOf('\n');
            if (lastNewline < 0)
            {
                CurrentLineLength += s.Length;
                return;
            }

            foreach (var c in s)
                if (c == '\n')
                    Row++;
            CurrentLineLength = s.Length - lastNewline - 1;
        }

        public void Append(char c) => Append(c.ToString());

        public int Length => _sb.Length;
        public char this[int index] => _sb[index];

        public override string ToString() => _sb.ToString();
    }

    private static void AppendChildren(HtmlNode node, Context ctx)
    {
        foreach (var child in node.ChildNodes)
            AppendNode(child, ctx);
    }

    private static void AppendNode(HtmlNode node, Context ctx)
    {
        if (node.NodeType == HtmlNodeType.Comment)
            return;

        if (node.NodeType == HtmlNodeType.Text)
        {
            var raw = HtmlEntity.DeEntitize(node.InnerText);
            var leadingSpace = raw.Length > 0 && char.IsWhiteSpace(raw[0]);
            var trailingSpace = raw.Length > 0 && char.IsWhiteSpace(raw[^1]);
            var collapsed = CollapseWhitespace(raw);

            if (collapsed.Length == 0)
            {
                // A whitespace-only node (source indentation between tags, or a literal single
                // space between two elements - e.g. "<A>1.</A> <A>Overview</A>") has no visible text
                // of its own, but if it held any whitespace at all, that still means a real word
                // coming up needs a separating space before it - see Context.PendingSpace.
                if (raw.Length > 0)
                    ctx.PendingSpace = true;
                return;
            }

            AppendBlockText(ctx, collapsed, leadingSpace || ctx.PendingSpace);
            ctx.PendingSpace = trailingSpace;
            return;
        }

        switch (node.Name.ToLowerInvariant())
        {
            case "head":
            case "title":
            case "script":
            case "style":
                return;

            case "a":
                AppendAnchor(ctx, node);
                return;

            case "b":
            case "strong":
                AppendStyledSpan(ctx, node, DocSpanKind.Bold);
                return;

            case "em":
            case "i":
                AppendStyledSpan(ctx, node, DocSpanKind.Emphasis);
                return;

            case "code":
            case "tt":
                AppendStyledSpan(ctx, node, DocSpanKind.Code);
                return;

            case "h1":
                AppendHeading(ctx, node, '=');
                return;
            case "h2":
            case "h3":
                AppendHeading(ctx, node, '-');
                return;

            case "p":
            case "center":
            case "blockquote":
            case "div":
                EnsureBlankLine(ctx.Writer);
                AppendChildren(node, ctx);
                EnsureBlankLine(ctx.Writer);
                return;

            case "br":
                ctx.Writer.Append('\n');
                ctx.Writer.Append(ctx.Indent);
                return;

            case "hr":
                EnsureBlankLine(ctx.Writer);
                ctx.Writer.Append(new string('-', WrapWidth));
                ctx.Writer.Append('\n');
                EnsureBlankLine(ctx.Writer);
                return;

            case "pre":
                EnsureBlankLine(ctx.Writer);
                var preStartRow = ctx.Writer.Row;
                foreach (var line in HtmlEntity.DeEntitize(node.InnerText).TrimEnd('\n', '\r').Split('\n'))
                {
                    ctx.Writer.Append(ctx.Indent);
                    ctx.Writer.Append("    ");
                    ctx.Writer.Append(line.TrimEnd('\r'));
                    ctx.Writer.Append('\n');
                }
                // ctx.Writer.Row has already moved past the block's last line (each line above ends
                // with '\n'), so the block's own last row is one behind it - unless the block was
                // empty (no lines at all, preStartRow == current Row), which BlockSpans has no use for.
                if (ctx.Writer.Row > preStartRow)
                    ctx.BlockSpans.Add(new DocBlockSpan(preStartRow, ctx.Writer.Row - 1, DocSpanKind.Code));
                EnsureBlankLine(ctx.Writer);
                return;

            case "ul":
            case "ol":
                EnsureBlankLine(ctx.Writer);
                AppendListItems(ctx, node, ordered: string.Equals(node.Name, "ol", StringComparison.OrdinalIgnoreCase));
                EnsureBlankLine(ctx.Writer);
                return;

            case "dl":
                EnsureBlankLine(ctx.Writer);
                AppendDefinitionList(ctx, node);
                EnsureBlankLine(ctx.Writer);
                return;

            case "table":
                EnsureBlankLine(ctx.Writer);
                AppendTable(ctx, node);
                EnsureBlankLine(ctx.Writer);
                return;

            default:
                // Any other inline or unrecognized element: just keep its text content flowing into
                // the surrounding paragraph, unstyled.
                AppendChildren(node, ctx);
                return;
        }
    }

    /// <summary>
    /// Handles both roles an <c>&lt;A&gt;</c> can play in these manuals - sometimes both at once
    /// (<c>&lt;A NAME="apple-def-cfg"&gt;&lt;/A&gt; &lt;A NAME="ss4.1"&gt;4.1&lt;/A&gt; &lt;A HREF="..."&gt;...&lt;/A&gt;</c>
    /// on one heading): a <c>NAME</c> records this row as that anchor's target (<see cref="Context.AnchorRows"/>),
    /// and an internal <c>HREF</c> (resolved by <see cref="ResolveInternalHref"/>) records the
    /// rendered span as a <see cref="DocLink"/> once its (inline) content has been appended like
    /// normal text - so its wrapping, whitespace-collapsing etc. all match plain prose exactly.
    /// A link whose text happens to wrap onto a second row (its target is still reachable via
    /// wherever else in the document links to the same place - just not highlightable/clickable at
    /// this particular occurrence) is silently dropped rather than mis-highlighting the wrong text.
    /// </summary>
    private static void AppendAnchor(Context ctx, HtmlNode node)
    {
        var name = node.GetAttributeValue("name", "");
        if (!string.IsNullOrEmpty(name))
            ctx.AnchorRows[name] = ctx.Writer.Row;

        var target = ResolveInternalHref(node.GetAttributeValue("href", ""), ctx.CurrentFileName);
        if (target is not { } t || !ctx.KnownFileNames.Contains(t.FileName))
        {
            AppendChildren(node, ctx);
            return;
        }

        var (row, column, length) = CaptureInlineSpan(ctx, node);
        if (length > 0)
            ctx.Links.Add(new DocLink(row, column, length, t.FileName, t.Anchor));
    }

    /// <summary><c>&lt;B&gt;</c>/<c>&lt;STRONG&gt;</c>/<c>&lt;EM&gt;</c>/<c>&lt;I&gt;</c>/inline
    /// <c>&lt;CODE&gt;</c>/<c>&lt;TT&gt;</c>: records the rendered span as a <see cref="DocSpan"/>
    /// once its (inline) content has been appended like normal text, same approach as
    /// <see cref="AppendAnchor"/>'s <see cref="DocLink"/> - see <see cref="CaptureInlineSpan"/>.</summary>
    private static void AppendStyledSpan(Context ctx, HtmlNode node, DocSpanKind kind)
    {
        var (row, column, length) = CaptureInlineSpan(ctx, node);
        if (length > 0)
            ctx.Spans.Add(new DocSpan(row, column, length, kind));
    }

    /// <summary>
    /// Appends <paramref name="node"/>'s (inline) content like normal text - so its wrapping,
    /// whitespace-collapsing etc. all match plain prose exactly - and returns where it landed, for
    /// <see cref="AppendAnchor"/>/<see cref="AppendStyledSpan"/> to record as a <see cref="DocLink"/>
    /// or <see cref="DocSpan"/>. A span whose text happens to wrap onto a second row (still fully
    /// readable, just not a precisely locatable single-row range - its target, for a link, is still
    /// reachable via wherever else in the document links to the same place) comes back with
    /// <c>Length</c> 0 rather than mis-locating/mis-highlighting the wrong text; callers skip
    /// recording anything in that case.
    /// </summary>
    private static (int Row, int Column, int Length) CaptureInlineSpan(Context ctx, HtmlNode node)
    {
        var startRow = ctx.Writer.Row;
        var startColumn = ctx.Writer.CurrentLineLength;
        var startIndex = ctx.Writer.Length;
        AppendChildren(node, ctx);

        // A separator space this span's own first word needed (e.g. the space between "1." and a
        // following "Overview" link in "1. Overview") is written from inside the AppendChildren call
        // just above, landing inside [startIndex, ...) - trim it so the recorded span starts at the
        // actual visible text, not the space before it.
        while (startIndex < ctx.Writer.Length && ctx.Writer[startIndex] == ' ')
        {
            startIndex++;
            startColumn++;
        }

        var length = ctx.Writer.Row == startRow ? ctx.Writer.Length - startIndex : 0;
        return (startRow, startColumn, length);
    }

    /// <summary>
    /// Resolves an <c>HREF</c> to a bundled page + optional anchor, or null if it isn't an internal
    /// link this viewer can navigate to (an external http(s)/mailto URL, or - defensively - anything
    /// with no path at all). Handles both forms these manuals use for a same-page link: a bare
    /// <c>#anchor</c>, and <c>currentFileName.html#anchor</c> (what the generator actually emits for
    /// same-page section links in most of these pages - see the doc comment on
    /// <see cref="DocTextConverter"/>). The caller still checks the resolved file name against the
    /// known bundle (a defensive backstop against an href to a page cc65's docs don't ship).
    /// </summary>
    private static (string FileName, string? Anchor)? ResolveInternalHref(string href, string currentFileName)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return null;

        var hashIndex = href.IndexOf('#');
        var filePart = hashIndex >= 0 ? href[..hashIndex] : href;
        var anchorPart = hashIndex >= 0 ? href[(hashIndex + 1)..] : "";

        var fileName = filePart.Length == 0 ? currentFileName : Path.GetFileNameWithoutExtension(filePart);
        return (fileName, anchorPart.Length == 0 ? null : anchorPart);
    }

    private static void AppendHeading(Context ctx, HtmlNode node, char underline)
    {
        EnsureBlankLine(ctx.Writer);
        var startRow = ctx.Writer.Row;
        var startIndex = ctx.Writer.Length;

        // Recurses like any other container (rather than flattening via node.InnerText, as before
        // this feature) so a heading's own <A NAME>/<A HREF> - very common; see this file's own doc
        // comment - are captured the same way as everywhere else. Headings in these manuals are
        // always short enough that this never actually wraps in practice.
        AppendChildren(node, ctx);

        var length = ctx.Writer.Row == startRow ? ctx.Writer.Length - startIndex : 0;
        if (length > 0)
            ctx.Spans.Add(new DocSpan(startRow, 0, length, DocSpanKind.Bold));
        ctx.Writer.Append('\n');
        if (length > 0)
        {
            ctx.Writer.Append(new string(underline, length));
            ctx.Writer.Append('\n');
        }
        EnsureBlankLine(ctx.Writer);
    }

    private static void AppendListItems(Context ctx, HtmlNode list, bool ordered)
    {
        var index = 1;
        foreach (var item in list.ChildNodes.Where(n => string.Equals(n.Name, "li", StringComparison.OrdinalIgnoreCase)))
        {
            var marker = ordered ? $"{index++}. " : "- ";
            ctx.Writer.Append(marker);

            var previousIndent = ctx.Indent;
            ctx.Indent = new string(' ', marker.Length);
            AppendChildren(item, ctx);
            ctx.Indent = previousIndent;

            ctx.Writer.Append('\n');
        }
    }

    private static void AppendDefinitionList(Context ctx, HtmlNode dl)
    {
        foreach (var child in dl.ChildNodes)
        {
            if (string.Equals(child.Name, "dt", StringComparison.OrdinalIgnoreCase))
            {
                AppendChildren(child, ctx);
                if (ctx.Writer.CurrentLineLength > 0)
                    ctx.Writer.Append('\n');
            }
            else if (string.Equals(child.Name, "dd", StringComparison.OrdinalIgnoreCase))
            {
                var previousIndent = ctx.Indent;
                ctx.Indent = "    ";
                ctx.Writer.Append(ctx.Indent);
                AppendChildren(child, ctx);
                ctx.Indent = previousIndent;

                ctx.Writer.Append('\n');
                ctx.Writer.Append('\n');
            }
        }
    }

    /// <summary>Flattens each cell to plain text (no link-tracking - see this file's own doc
    /// comment) since a whole row's cells are joined onto one line with no sensible place to hang a
    /// wrapped link's continuation anyway. Tables are rare across the bundled manuals.</summary>
    private static void AppendTable(Context ctx, HtmlNode table)
    {
        foreach (var row in table.Descendants("tr"))
        {
            var cells = row.ChildNodes
                .Where(n => n.Name is "td" or "th")
                .Select(n => CollapseWhitespace(HtmlEntity.DeEntitize(n.InnerText)).Trim());
            ctx.Writer.Append(string.Join("  |  ", cells));
            ctx.Writer.Append('\n');
        }
    }

    /// <summary>Appends already-collapsed, non-empty text into the current paragraph, word-wrapping
    /// the running line at <see cref="WrapWidth"/> and indenting any wrapped continuation line by
    /// <see cref="Context.Indent"/>. <paramref name="leadingSpace"/> says whether a space belongs
    /// between whatever was written last and this text's first word - the source HTML's own
    /// whitespace (or lack of it) between elements, not a guess (see <see cref="Context.PendingSpace"/>),
    /// so adjacent inline elements with no space between them in the source (e.g. <c>(&lt;CODE&gt;x&lt;/CODE&gt;)</c>)
    /// don't get one invented.</summary>
    private static void AppendBlockText(Context ctx, string text, bool leadingSpace)
    {
        var w = ctx.Writer;
        var separator = w.CurrentLineLength > 0 && leadingSpace ? " " : "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w.CurrentLineLength > 0 && w.CurrentLineLength + separator.Length + word.Length > WrapWidth)
            {
                w.Append('\n');
                w.Append(ctx.Indent);
                separator = "";
            }
            w.Append(separator);
            w.Append(word);
            separator = " ";
        }
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

    /// <summary>
    /// Collapses runs of 2+ blank lines down to exactly one, renumbering every recorded row (links,
    /// anchors, spans, block spans) as each removed line shifts everything below it up - a plain
    /// <c>string.Replace</c> (as this used before any of that existed) would silently desync them
    /// from the text it rewrote out from under them.
    /// </summary>
    private static DocConversionResult CollapseBlankLines(Context ctx)
    {
        var lines = ctx.Writer.ToString().Split('\n');
        var outputLines = new List<string>(lines.Length);
        var rowMap = new int[lines.Length + 1]; // +1: a link/anchor can legitimately sit on a trailing empty "row" past the last '\n'.

        var previousWasBlank = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var isBlank = lines[i].Length == 0;
            if (isBlank && previousWasBlank)
            {
                rowMap[i] = outputLines.Count - 1; // collapsed into the previous (already-emitted) blank line
                continue;
            }

            rowMap[i] = outputLines.Count;
            outputLines.Add(lines[i]);
            previousWasBlank = isBlank;
        }
        rowMap[lines.Length] = outputLines.Count;

        // Leading/trailing blank lines (from the outermost block's own EnsureBlankLine calls) are
        // trimmed same as the old string.Trim('\n') did - tracked as an offset so row numbers below
        // still line up with the trimmed text.
        var start = 0;
        while (start < outputLines.Count && outputLines[start].Length == 0)
            start++;
        var end = outputLines.Count;
        while (end > start && outputLines[end - 1].Length == 0)
            end--;

        var text = string.Join('\n', outputLines.GetRange(start, end - start)) + "\n";

        // rowMap can point a link/anchor made irrelevant by trimming (e.g. an anchor on a line that
        // turned out to be trailing whitespace) outside [0, lastRow] - clamp rather than let it
        // address a row DocViewerShell's InsertionPoint can't actually scroll to.
        var lastRow = Math.Max(0, end - start - 1);
        int MapRow(int row) => Math.Clamp(rowMap[row] - start, 0, lastRow);

        var links = ctx.Links.Select(l => l with { Row = MapRow(l.Row) }).ToList();
        var anchorRows = ctx.AnchorRows.ToDictionary(kv => kv.Key, kv => MapRow(kv.Value));
        var spans = ctx.Spans.Select(s => s with { Row = MapRow(s.Row) }).ToList();
        var blockSpans = ctx.BlockSpans.Select(b => b with { StartRow = MapRow(b.StartRow), EndRow = MapRow(b.EndRow) }).ToList();

        return new DocConversionResult(text, links, anchorRows, spans, blockSpans);
    }
}
