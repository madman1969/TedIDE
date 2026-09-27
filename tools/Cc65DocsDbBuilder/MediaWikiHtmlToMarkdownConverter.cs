using System.Text;
using HtmlAgilityPack;

namespace Cc65DocsDbBuilder;

/// <summary>
/// A MediaWiki site whose articles are bundled: the page-id prefix its articles get in Docs.db
/// (<c>c64wiki/</c>, <c>wikipedia/</c>), the redirect titles that lead to bundled articles, and
/// section headings whose content is left out entirely.
/// </summary>
public sealed record MediaWikiSite(string PageIdPrefix, IReadOnlyDictionary<string, string> Redirects, IReadOnlySet<string> DroppedSections);

/// <summary>
/// Converts one MediaWiki article - from C64-Wiki (see <see cref="C64WikiPageCatalog"/>) or
/// Wikipedia (see <see cref="WikipediaPageCatalog"/>) - to Markdown for
/// <c>Terminal.Gui.Views.Markdown</c>. The source files hold just the article body MediaWiki
/// rendered (<c>div.mw-parser-output</c>) plus the page title, so extraction is simple; what needs
/// care is MediaWiki's own markup inside it:
///
/// - The page title isn't part of the body, so it's emitted as the page's H1; the body's own
///   h2/h3/h4 follow. A heading's text and anchor come from its <c>span.mw-headline</c> (older
///   MediaWiki, C64-Wiki) or the heading element's own id (newer, Wikipedia); its "[edit]" link
///   (<c>span.mw-editsection</c>) is dropped.
/// - Dropped as navigation or media rather than text: the contents box (<c>div#toc</c> - DocViewer
///   has no use for a second table of contents), images and their captions (<c>figure</c>,
///   <c>img</c>, <c>div.thumb</c>, galleries), citation markers and reference lists, hatnotes,
///   navigation boxes, maintenance banners and print-only footers. A site can also drop whole
///   sections by heading (<see cref="MediaWikiSite.DroppedSections"/> - Wikipedia's "References",
///   "External links" and the like are lists of citations with nothing to follow offline).
/// - Links are MediaWiki paths, <c>/wiki/Page_name#Section</c>. One to a bundled article - directly
///   or through a known redirect (<see cref="MediaWikiSite.Redirects"/>) - becomes a page link
///   (<c>c64wiki/Page_name.html#slug</c>, or <c>#slug</c> on the same page), resolved through
///   <see cref="BuildAnchorIndex"/> the same way the cc65 manuals' and The C Book's are. Any other
///   link - a page not bundled, a missing ("red") page, an external site - keeps its text only:
///   DocViewer can't follow those anyway.
/// - Lists nest (MediaWiki's <c>**</c> items), so nested lists are indented rather than flattened;
///   table cells spanning columns are padded out so a row still lines up with its header.
/// </summary>
public static class MediaWikiHtmlToMarkdownConverter
{
    /// <summary>A page's heading anchors, mapped to the slug Terminal.Gui's Markdown view will
    /// compute for that heading.</summary>
    public sealed class PageAnchors
    {
        public required Dictionary<string, string> SlugsByAnchorName { get; init; }
    }

    /// <summary>The article's title, from the source file's <c>&lt;title&gt;</c>.</summary>
    public static string Title(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return FlattenText(document.DocumentNode.SelectSingleNode("//title")
            ?? throw new InvalidOperationException("Article has no <title>."));
    }

    /// <summary>A <c>&lt;meta name="..."&gt;</c> value from the source file's header (its page
    /// name, revision and last-edited date - see the fetch notes at the top of each file).</summary>
    public static string Meta(string html, string name)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document.DocumentNode.SelectSingleNode($"//meta[@name='{name}']")?.GetAttributeValue("content", "") ?? "";
    }

    /// <summary>Pass 1: each page's heading anchors, in the order the headings will be emitted -
    /// the title first, then the body's - so repeated headings get the same "-1", "-2" suffixes
    /// the view gives them.</summary>
    public static Dictionary<string, PageAnchors> BuildAnchorIndex(IReadOnlyDictionary<string, string> htmlByPageId, MediaWikiSite site)
    {
        var index = new Dictionary<string, PageAnchors>();
        foreach (var (pageId, html) in htmlByPageId)
        {
            var slugUseCount = new Dictionary<string, int>();
            MarkdownSlug.MakeUnique(Title(html), slugUseCount);

            var slugsByAnchorName = new Dictionary<string, string>();
            var headings = KeptContent(LoadContent(html), site)
                .SelectMany(n => n.DescendantsAndSelf())
                .Where(n => IsHeading(n) && !IsSkipped(n));
            foreach (var heading in headings)
            {
                var text = HeadingText(heading);
                if (text.Length == 0)
                    continue;

                var slug = MarkdownSlug.MakeUnique(text, slugUseCount);
                foreach (var name in HeadingAnchorNames(heading))
                    slugsByAnchorName[name] = slug;
            }
            index[pageId] = new PageAnchors { SlugsByAnchorName = slugsByAnchorName };
        }
        return index;
    }

    /// <summary>Pass 2: the article as Markdown, links resolved through <paramref name="anchorIndex"/>.</summary>
    public static string Convert(string html, string currentPageId, IReadOnlyDictionary<string, PageAnchors> anchorIndex, MediaWikiSite site)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(Title(html)).Append("\n\n");

        var ctx = new Context(sb, currentPageId, anchorIndex, site);
        foreach (var child in KeptContent(LoadContent(html), site))
            AppendNode(child, ctx);

        var text = sb.ToString();
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
            text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        return text.Trim('\n') + "\n";
    }

    private static HtmlNode LoadContent(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document.DocumentNode.SelectSingleNode("//div[contains(concat(' ', normalize-space(@class), ' '), ' mw-parser-output ')]")
            ?? throw new InvalidOperationException("Article has no div.mw-parser-output.");
    }

    /// <summary>
    /// The body's top-level nodes, minus any section the site drops by heading (see
    /// <see cref="MediaWikiSite.DroppedSections"/>): from that heading up to the next heading of the
    /// same or a higher level. A heading is either a bare h2-h5 (older MediaWiki) or wrapped in
    /// <c>div.mw-heading</c> alongside its edit link (newer).
    /// </summary>
    private static IEnumerable<HtmlNode> KeptContent(HtmlNode content, MediaWikiSite site)
    {
        int? droppingBelowLevel = null;
        foreach (var child in content.ChildNodes)
        {
            var heading = IsHeading(child) ? child
                : child.Name == "div" && HasClass(child, "mw-heading") ? child.ChildNodes.FirstOrDefault(IsHeading)
                : null;
            if (heading is not null)
            {
                var level = heading.Name[1] - '0';
                if (droppingBelowLevel is { } dropping && level <= dropping)
                    droppingBelowLevel = null;
                if (droppingBelowLevel is null && site.DroppedSections.Contains(HeadingText(heading)))
                    droppingBelowLevel = level;
            }
            if (droppingBelowLevel is null)
                yield return child;
        }
    }

    private sealed class Context(StringBuilder sb, string currentPageId, IReadOnlyDictionary<string, PageAnchors> anchorIndex, MediaWikiSite site)
    {
        public readonly StringBuilder Sb = sb;
        public readonly string CurrentPageId = currentPageId;
        public readonly IReadOnlyDictionary<string, PageAnchors> AnchorIndex = anchorIndex;
        public readonly MediaWikiSite Site = site;

        /// <summary>Whether the last text appended ended in whitespace - see
        /// <see cref="HtmlToMarkdownConverter"/>'s equivalent.</summary>
        public bool PendingSpace;

        /// <summary>Set inside <c>&lt;code&gt;</c>, so text isn't Markdown-escaped.</summary>
        public bool InCode;

        /// <summary>Current list nesting depth, for indenting nested list items.</summary>
        public int ListDepth;
    }

    private static bool IsHeading(HtmlNode node) => node.Name is "h2" or "h3" or "h4" or "h5";

    private static bool HasClass(HtmlNode node, string cssClass) =>
        $" {node.GetAttributeValue("class", "")} ".Contains($" {cssClass} ", StringComparison.Ordinal);

    /// <summary>Navigation, media and markers with no place in the Markdown - see the class comment.</summary>
    private static bool IsSkipped(HtmlNode node) =>
        node.Name is "script" or "style" or "figure" or "img" or "input" or "label" or "noscript"
        || node.GetAttributeValue("id", "") == "toc"
        || HasClass(node, "mw-editsection") || HasClass(node, "thumb") || HasClass(node, "gallery")
        || HasClass(node, "reference") || HasClass(node, "printfooter") || HasClass(node, "noprint")
        || HasClass(node, "mw-empty-elt")
        // Wikipedia's: reference lists, "For other uses..." hatnotes, navigation boxes and
        // sidebars, and maintenance banners ("This article needs additional citations...").
        || HasClass(node, "reflist") || HasClass(node, "references") || HasClass(node, "mw-references-wrap")
        || HasClass(node, "hatnote") || HasClass(node, "navbox") || HasClass(node, "sidebar")
        || HasClass(node, "ambox") || HasClass(node, "metadata") || HasClass(node, "shortdescription")
        || node.Name == "link"
        || node.Ancestors().Any(a => a.GetAttributeValue("id", "") == "toc" || HasClass(a, "mw-editsection"));

    /// <summary>A heading's text: its <c>span.mw-headline</c> when present (the rest of the element
    /// is the "[edit]" link), else its own text.</summary>
    private static string HeadingText(HtmlNode heading)
    {
        var headline = heading.Descendants("span").FirstOrDefault(s => HasClass(s, "mw-headline"));
        return FlattenText(headline ?? heading);
    }

    private static IEnumerable<string> HeadingAnchorNames(HtmlNode heading) =>
        new[] { heading }.Concat(heading.Descendants())
            .Select(n => n.GetAttributeValue("id", ""))
            .Where(id => id.Length > 0)
            .Distinct();

    private static void AppendChildren(HtmlNode node, Context ctx)
    {
        foreach (var child in node.ChildNodes)
            AppendNode(child, ctx);
    }

    private static void AppendNode(HtmlNode node, Context ctx)
    {
        if (node.NodeType == HtmlNodeType.Comment || IsSkipped(node))
            return;

        if (node.NodeType == HtmlNodeType.Text)
        {
            var raw = HtmlEntity.DeEntitize(node.InnerText);
            var leadingSpace = raw.Length > 0 && char.IsWhiteSpace(raw[0]);
            var trailingSpace = raw.Length > 0 && char.IsWhiteSpace(raw[^1]);
            var collapsed = CollapseWhitespace(raw);
            if (collapsed.Length == 0)
            {
                if (raw.Length > 0)
                    ctx.PendingSpace = true;
                return;
            }
            AppendInlineText(ctx, collapsed, leadingSpace || ctx.PendingSpace);
            ctx.PendingSpace = trailingSpace;
            return;
        }

        switch (node.Name)
        {
            case "a":
                AppendAnchor(ctx, node);
                return;

            case "h2" or "h3" or "h4" or "h5":
                AppendHeading(ctx, node);
                return;

            case "b" or "strong":
                WrapInline(ctx, node, "**", "**");
                return;

            case "i" or "em":
                WrapInline(ctx, node, "*", "*");
                return;

            case "code" or "tt" or "kbd" or "samp":
                AppendCode(ctx, node);
                return;

            case "p" or "div" or "center" or "blockquote":
                EnsureBlankLine(ctx.Sb);
                AppendChildren(node, ctx);
                EnsureBlankLine(ctx.Sb);
                return;

            case "br":
                ctx.Sb.Append("  \n");
                return;

            case "hr":
                EnsureBlankLine(ctx.Sb);
                ctx.Sb.Append("---\n");
                EnsureBlankLine(ctx.Sb);
                return;

            case "pre":
                // C64-Wiki's listings are BASIC, 6502 assembly or plain text, never marked with a
                // language - so no fence language, rather than a wrong one.
                EnsureBlankLine(ctx.Sb);
                ctx.Sb.Append("```\n");
                ctx.Sb.Append(HtmlEntity.DeEntitize(node.InnerText).Trim('\n', '\r'));
                ctx.Sb.Append("\n```\n");
                EnsureBlankLine(ctx.Sb);
                return;

            case "ul" or "ol":
                if (ctx.ListDepth == 0)
                    EnsureBlankLine(ctx.Sb);
                AppendListItems(ctx, node, ordered: node.Name == "ol");
                if (ctx.ListDepth == 0)
                    EnsureBlankLine(ctx.Sb);
                return;

            case "dl":
                EnsureBlankLine(ctx.Sb);
                AppendDefinitionList(ctx, node);
                EnsureBlankLine(ctx.Sb);
                return;

            case "table":
                EnsureBlankLine(ctx.Sb);
                AppendTable(ctx, node);
                EnsureBlankLine(ctx.Sb);
                return;

            default:
                AppendChildren(node, ctx);
                return;
        }
    }

    private static void WrapInline(Context ctx, HtmlNode node, string open, string close)
    {
        if (ctx.PendingSpace && ctx.Sb.Length > 0 && ctx.Sb[^1] is not ('\n' or ' '))
            ctx.Sb.Append(' ');
        ctx.PendingSpace = false;

        var startLength = ctx.Sb.Length;
        ctx.Sb.Append(open);
        AppendChildren(node, ctx);
        // Emphasis markers can't sit next to whitespace, so trim what the content ended with.
        while (ctx.Sb.Length > startLength + open.Length && ctx.Sb[^1] == ' ')
        {
            ctx.Sb.Length--;
            ctx.PendingSpace = true;
        }
        if (ctx.Sb.Length == startLength + open.Length)
        {
            ctx.Sb.Length = startLength;
            return;
        }
        ctx.Sb.Append(close);
    }

    private static void AppendCode(Context ctx, HtmlNode node)
    {
        var wasInCode = ctx.InCode;
        ctx.InCode = true;
        WrapInline(ctx, node, "`", "`");
        ctx.InCode = wasInCode;
    }

    private static void AppendAnchor(Context ctx, HtmlNode node)
    {
        var url = HasClass(node, "new") ? null : ResolveHref(node.GetAttributeValue("href", ""), ctx.CurrentPageId, ctx.AnchorIndex, ctx.Site);
        if (url is null)
        {
            AppendChildren(node, ctx);
            return;
        }

        if (ctx.PendingSpace && ctx.Sb.Length > 0 && ctx.Sb[^1] is not ('\n' or ' '))
            ctx.Sb.Append(' ');
        ctx.PendingSpace = false;

        ctx.Sb.Append('[');
        var startLength = ctx.Sb.Length;
        AppendChildren(node, ctx);
        if (ctx.Sb.Length == startLength)
        {
            ctx.Sb.Length--; // no link text (an image link, say) - drop the link entirely.
            return;
        }
        ctx.Sb.Append("](").Append(url).Append(')');
    }

    /// <summary>
    /// The DocViewer link for a MediaWiki <paramref name="href"/> (<c>/wiki/VIC-II#Registers</c>),
    /// or null if it doesn't lead to a bundled article. A section that can't be matched to a
    /// heading still links to the page itself.
    /// </summary>
    public static string? ResolveHref(string href, string currentPageId, IReadOnlyDictionary<string, PageAnchors> anchorIndex, MediaWikiSite site)
    {
        href = HtmlEntity.DeEntitize(href);
        string pageName;
        string fragment;
        if (href.StartsWith('#'))
        {
            pageName = currentPageId[site.PageIdPrefix.Length..];
            fragment = href[1..];
        }
        else if (href.StartsWith("/wiki/", StringComparison.Ordinal))
        {
            var target = href["/wiki/".Length..];
            var hash = target.IndexOf('#');
            pageName = Uri.UnescapeDataString(hash >= 0 ? target[..hash] : target);
            fragment = hash >= 0 ? target[(hash + 1)..] : "";
        }
        else
        {
            return null; // external, or an index.php?... action link.
        }

        if (site.Redirects.TryGetValue(pageName, out var redirectTarget))
            pageName = redirectTarget;

        var pageId = site.PageIdPrefix + pageName;
        if (!anchorIndex.TryGetValue(pageId, out var anchors))
            return null;

        var slug = fragment.Length > 0 && anchors.SlugsByAnchorName.TryGetValue(Uri.UnescapeDataString(fragment), out var s) ? s : null;
        if (pageId == currentPageId)
            return slug is null ? null : $"#{slug}";
        return slug is null ? $"{pageId}.html" : $"{pageId}.html#{slug}";
    }

    private static void AppendHeading(Context ctx, HtmlNode node)
    {
        var text = HeadingText(node);
        if (text.Length == 0)
            return;

        // h2 is a section of the article (whose own title is the H1).
        var level = node.Name[1] - '0';
        EnsureBlankLine(ctx.Sb);
        ctx.Sb.Append(new string('#', level)).Append(' ').Append(text).Append('\n');
        EnsureBlankLine(ctx.Sb);
        ctx.PendingSpace = false;
    }

    private static void AppendListItems(Context ctx, HtmlNode list, bool ordered)
    {
        var number = 1;
        var indent = new string(' ', 2 * ctx.ListDepth);
        foreach (var item in list.ChildNodes.Where(n => n.Name == "li" && !IsSkipped(n)))
        {
            if (ctx.Sb.Length > 0 && ctx.Sb[^1] != '\n')
                ctx.Sb.Append('\n');
            ctx.Sb.Append(indent).Append(ordered ? $"{number++}. " : "- ");
            ctx.PendingSpace = false;

            foreach (var child in item.ChildNodes)
            {
                if (child.Name is "ul" or "ol")
                {
                    ctx.ListDepth++;
                    AppendNode(child, ctx);
                    ctx.ListDepth--;
                }
                else
                {
                    AppendNode(child, ctx);
                }
            }
            if (ctx.Sb.Length > 0 && ctx.Sb[^1] != '\n')
                ctx.Sb.Append('\n');
        }
    }

    private static void AppendDefinitionList(Context ctx, HtmlNode dl)
    {
        foreach (var child in dl.ChildNodes)
        {
            if (child.Name == "dt")
            {
                ctx.Sb.Append("**");
                var startLength = ctx.Sb.Length;
                AppendChildren(child, ctx);
                if (ctx.Sb.Length == startLength)
                    ctx.Sb.Length -= 2;
                else
                    ctx.Sb.Append("**");
                ctx.Sb.Append('\n');
            }
            else if (child.Name == "dd")
            {
                // MediaWiki uses a bare <dd> for plain indentation too (":" in wikitext).
                AppendChildren(child, ctx);
                ctx.Sb.Append("\n\n");
            }
        }
        ctx.PendingSpace = false;
    }

    private static void AppendTable(Context ctx, HtmlNode table)
    {
        // This table's own rows only - a table nested in a cell is flattened into that cell's text.
        // Cells spanning several columns or rows leave blanks in the positions they cover, so every
        // row still lines up column for column (e.g. the Kernal article's two-row "Address" header).
        var rowSpansLeft = new Dictionary<int, int>(); // column -> rows still covered from above
        var rows = new List<List<string>>();
        foreach (var row in table.ChildNodes
            .SelectMany(n => n.Name is "tbody" or "thead" or "tfoot" ? n.ChildNodes.AsEnumerable() : [n])
            .Where(n => n.Name == "tr"))
        {
            var cells = new List<string>();
            void SkipCoveredColumns()
            {
                while (rowSpansLeft.TryGetValue(cells.Count, out var left) && left > 0)
                {
                    rowSpansLeft[cells.Count] = left - 1;
                    cells.Add("");
                }
            }

            foreach (var cell in row.ChildNodes.Where(c => c.Name is "td" or "th"))
            {
                SkipCoveredColumns();
                var colSpan = Math.Clamp(cell.GetAttributeValue("colspan", 1), 1, 20);
                var rowSpan = Math.Clamp(cell.GetAttributeValue("rowspan", 1), 1, 100);
                for (var i = 0; i < colSpan; i++)
                {
                    if (rowSpan > 1)
                        rowSpansLeft[cells.Count] = rowSpan - 1;
                    cells.Add(i == 0 ? CellText(cell) : "");
                }
            }
            SkipCoveredColumns();
            if (cells.Count > 0)
                rows.Add(cells);
        }
        // Rows with no text at all - an infobox's image row, say - would only print as empty lines.
        rows.RemoveAll(r => r.All(c => c.Length == 0));
        if (rows.Count == 0)
            return;

        // A layout table rather than a data one - e.g. a "Links" entry that's an icon beside its
        // text - reads better as a plain list of its text.
        if (rows.All(r => r.Count(c => c.Length > 0) <= 1))
        {
            foreach (var text in rows.Select(r => r.FirstOrDefault(c => c.Length > 0)).OfType<string>())
                ctx.Sb.Append("- ").Append(text).Append('\n');
            ctx.PendingSpace = false;
            return;
        }

        var columnCount = rows.Max(r => r.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].Concat(Enumerable.Repeat("", columnCount - rows[i].Count));
            ctx.Sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (i == 0)
                ctx.Sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columnCount))).Append('\n');
        }
        ctx.PendingSpace = false;
    }

    /// <summary>A cell's text on one line: inline elements run together as written ("(1977)", not
    /// "( 1977 )"), line breaks and block boundaries become spaces.</summary>
    private static string CellText(HtmlNode cell)
    {
        var sb = new StringBuilder();
        void Walk(HtmlNode node)
        {
            if (IsSkipped(node))
                return;
            if (node.NodeType == HtmlNodeType.Text)
            {
                sb.Append(HtmlEntity.DeEntitize(node.InnerText));
                return;
            }
            var isBlock = node.Name is "br" or "p" or "div" or "li" or "ul" or "ol" or "dl" or "dt" or "dd" or "table" or "tr" or "td" or "th";
            if (isBlock)
                sb.Append(' ');
            foreach (var child in node.ChildNodes)
                Walk(child);
            if (isBlock)
                sb.Append(' ');
        }
        foreach (var child in cell.ChildNodes)
            Walk(child);
        return CollapseWhitespace(sb.ToString()).Trim().Replace("|", "\\|");
    }

    private static void AppendInlineText(Context ctx, string text, bool leadingSpace)
    {
        if (ctx.Sb.Length > 0 && leadingSpace && ctx.Sb[^1] is not ('\n' or ' '))
            ctx.Sb.Append(' ');
        ctx.Sb.Append(ctx.InCode ? text : EscapeMarkdown(text));
    }

    private static string EscapeMarkdown(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '*' or '_' or '[' or ']' or '`' or '\\')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void EnsureBlankLine(StringBuilder sb)
    {
        if (sb.Length == 0)
            return;
        if (sb[^1] != '\n')
            sb.Append('\n');
        if (sb.Length < 2 || sb[^2] != '\n')
            sb.Append('\n');
    }

    private static string FlattenText(HtmlNode node) =>
        CollapseWhitespace(HtmlEntity.DeEntitize(node.InnerText)).Trim();

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
