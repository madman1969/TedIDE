using System.Text;
using HtmlAgilityPack;

namespace Cc65DocsDbBuilder;

/// <summary>
/// Converts one page of "The C Book" (https://publications.gbdirect.co.uk/c_book/, bundled under
/// its own free-redistribution license - see the copyright page ingested as the "copyright" page
/// entry) to Markdown for <c>Terminal.Gui.Views.Markdown</c>. Block/inline element handling
/// mirrors <see cref="HtmlToMarkdownConverter"/> (the same HTML vocabulary - paragraphs, lists,
/// tables, code, emphasis - needs the same rendering regardless of which site it came from), but
/// this book's own page structure is different enough to need its own extraction and link
/// resolution rather than sharing that class's engine directly:
///
/// - Real content is every child of <c>&lt;main id="main"&gt;</c> except the <c>&lt;nav
///   id="breadcrumb"&gt;</c> at the top, a stray <c>&lt;br&gt;</c> right after it, and a trailing
///   <c>&lt;div class="cbook_next_prev"&gt;</c> (Previous/Next links - redundant with DocViewer's
///   own tree). The site's left-hand chapter menu and right-hand sidebar box are siblings of
///   <c>&lt;main&gt;</c>, not descendants, so they're already excluded just by scoping to it.
/// - Every page's own H1/H2/H3 is real content (unlike the cc65 manuals, this book has no
///   in-page table-of-contents heading to skip - each chapter's own ToC lives on a separate
///   "index" page, e.g. <c>chapter5/</c>, which is never ingested in the first place: it's pure
///   navigation with nothing DocViewer's own category tree doesn't already show).
/// - Cross-references are ordinary relative hrefs (<c>pointers.html</c>, <c>../chapter6/</c>)
///   rather than the cc65 manuals' flat <c>file.html#anchor</c> - <see cref="ResolvePageId"/>
///   resolves them against the current page's own chapter directory. A link to an excluded index
///   page (any href that doesn't end in <c>.html</c> once normalized) has no ingested target and
///   falls back to plain text, the same graceful degradation
///   <see cref="HtmlToMarkdownConverter"/> uses for any other unresolvable link.
/// - Non-heading anchors this book also defines (<c>example-N</c>, <c>figure-N</c>, <c>table-N</c>,
///   <c>exercise-N</c>, <c>footN</c>) are never in <see cref="PageAnchors"/> (only real headings
///   are), so a link to one simply can't resolve - same plain-text fallback, not a special case.
/// </summary>
public static class CBookHtmlToMarkdownConverter
{
    /// <summary>A page's heading anchors, mapped to the exact slug Terminal.Gui's Markdown view
    /// will independently compute for that heading at render time.</summary>
    public sealed class PageAnchors
    {
        public required Dictionary<string, string> SlugsByAnchorName { get; init; }
    }

    /// <summary>Pass 1: walks every page's H1/H2/H3 in document order, computing the slug Markdig's
    /// auto-identifier extension will assign each one - needed before any page can be converted,
    /// since a cross-page link has to resolve an anchor defined on a page not yet converted.</summary>
    public static Dictionary<string, PageAnchors> BuildAnchorIndex(IReadOnlyDictionary<string, string> htmlByPageId)
    {
        var index = new Dictionary<string, PageAnchors>();
        foreach (var (pageId, html) in htmlByPageId)
        {
            var main = LoadMainContent(html);
            var slugsByAnchorName = new Dictionary<string, string>();
            var slugUseCount = new Dictionary<string, int>();

            foreach (var heading in main.Descendants().Where(n => n.Name is "h1" or "h2" or "h3"))
            {
                var text = FlattenText(heading);
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

    /// <summary>Pass 2: renders <paramref name="html"/> as Markdown, resolving internal links via
    /// <paramref name="anchorIndex"/> (built by <see cref="BuildAnchorIndex"/> across every page
    /// first).</summary>
    public static string Convert(
        string html,
        string currentPageId,
        IReadOnlyDictionary<string, PageAnchors> anchorIndex,
        IReadOnlySet<string> knownPageIds)
    {
        var main = LoadMainContent(html);

        var sb = new StringBuilder();
        var ctx = new Context(sb, currentPageId, anchorIndex, knownPageIds);
        foreach (var child in RealContentChildren(main))
            AppendNode(child, ctx);

        var text = sb.ToString();
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
            text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        return text.Trim('\n') + "\n";
    }

    private static HtmlNode LoadMainContent(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document.DocumentNode.SelectSingleNode("//main[@id='main']")
            ?? throw new InvalidOperationException("Page has no <main id=\"main\">.");
    }

    /// <summary>Every child of <c>&lt;main&gt;</c> that's actual page content - see this class's
    /// own doc comment for what's excluded and why.</summary>
    private static IEnumerable<HtmlNode> RealContentChildren(HtmlNode main)
    {
        var afterBreadcrumb = false;
        foreach (var child in main.ChildNodes)
        {
            if (string.Equals(child.Name, "nav", StringComparison.OrdinalIgnoreCase))
            {
                afterBreadcrumb = true;
                continue;
            }
            if (!afterBreadcrumb && string.Equals(child.Name, "br", StringComparison.OrdinalIgnoreCase))
                continue; // the spacer right after the breadcrumb nav.
            if (string.Equals(child.Name, "div", StringComparison.OrdinalIgnoreCase) &&
                child.GetAttributeValue("class", "").Contains("cbook_next_prev", StringComparison.Ordinal))
                continue; // trailing Previous/Next-section links - redundant with the tree.

            yield return child;
        }
    }

    private sealed class Context(StringBuilder sb, string currentPageId, IReadOnlyDictionary<string, PageAnchors> anchorIndex, IReadOnlySet<string> knownPageIds)
    {
        public readonly StringBuilder Sb = sb;
        public readonly string CurrentPageId = currentPageId;
        public readonly IReadOnlyDictionary<string, PageAnchors> AnchorIndex = anchorIndex;
        public readonly IReadOnlySet<string> KnownPageIds = knownPageIds;

        /// <summary>Whether the most recently appended text ended in whitespace, or was
        /// whitespace-only - see <see cref="HtmlToMarkdownConverter"/>'s equivalent field for the
        /// full rationale.</summary>
        public bool PendingSpace;

        /// <summary>Set while appending a <c>&lt;CODE&gt;</c>/<c>&lt;TT&gt;</c> span's content, so
        /// <see cref="AppendInlineText"/> skips <see cref="EscapeMarkdown"/>.</summary>
        public bool InCode;
    }

    private static List<string> HeadingAnchorNames(HtmlNode heading) =>
        heading.Descendants("a")
            .Select(a => a.GetAttributeValue("name", ""))
            .Where(n => n.Length > 0)
            .ToList();

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
                if (raw.Length > 0)
                    ctx.PendingSpace = true;
                return;
            }

            AppendInlineText(ctx, collapsed, leadingSpace || ctx.PendingSpace);
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

            case "h1":
            case "h2":
            case "h3":
                AppendHeading(ctx, node);
                return;

            case "b":
            case "strong":
                WrapInline(ctx, node, "**", "**");
                return;

            case "em":
            case "i":
                WrapInline(ctx, node, "*", "*");
                return;

            case "code":
            case "tt":
                AppendCode(ctx, node);
                return;

            case "p":
            case "center":
            case "blockquote":
            case "div":
                EnsureBlankLine(ctx.Sb);
                AppendChildren(node, ctx);
                EnsureBlankLine(ctx.Sb);
                return;

            case "br":
                ctx.Sb.Append("  \n"); // Markdown hard line break (two trailing spaces).
                return;

            case "hr":
                EnsureBlankLine(ctx.Sb);
                ctx.Sb.Append("---\n");
                EnsureBlankLine(ctx.Sb);
                return;

            case "pre":
                // The original HTML never marks a snippet's language - "c" is a fixed default (see
                // HtmlToMarkdownConverter's own remark on this) since The C Book's own examples are,
                // unsurprisingly, overwhelmingly C.
                EnsureBlankLine(ctx.Sb);
                ctx.Sb.Append("```c\n");
                ctx.Sb.Append(HtmlEntity.DeEntitize(node.InnerText).TrimEnd('\n', '\r'));
                ctx.Sb.Append("\n```\n");
                EnsureBlankLine(ctx.Sb);
                return;

            case "ul":
            case "ol":
                EnsureBlankLine(ctx.Sb);
                AppendListItems(ctx, node, ordered: string.Equals(node.Name, "ol", StringComparison.OrdinalIgnoreCase));
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
        var startLength = ctx.Sb.Length;
        ctx.Sb.Append(open);
        AppendChildren(node, ctx);
        if (ctx.Sb.Length == startLength + open.Length)
        {
            ctx.Sb.Length = startLength;
            return;
        }
        ctx.Sb.Append(close);
    }

    private static void AppendCode(Context ctx, HtmlNode node)
    {
        if (node.Descendants("pre").Any())
        {
            AppendChildren(node, ctx);
            return;
        }

        var wasInCode = ctx.InCode;
        ctx.InCode = true;
        WrapInline(ctx, node, "`", "`");
        ctx.InCode = wasInCode;
    }

    private static void AppendAnchor(Context ctx, HtmlNode node)
    {
        var target = ResolveInternalHref(node.GetAttributeValue("href", ""), ctx.CurrentPageId, ctx.KnownPageIds, ctx.AnchorIndex);
        if (target is null)
        {
            AppendChildren(node, ctx);
            return;
        }

        if (ctx.PendingSpace && ctx.Sb.Length > 0 && ctx.Sb[^1] != '\n' && ctx.Sb[^1] != ' ')
            ctx.Sb.Append(' ');
        ctx.PendingSpace = false;

        ctx.Sb.Append('[');
        var startLength = ctx.Sb.Length;
        AppendChildren(node, ctx);
        if (ctx.Sb.Length == startLength)
            ctx.Sb.Append(target.PageId);
        ctx.Sb.Append("](").Append(target.Url).Append(')');
    }

    private sealed record ResolvedTarget(string PageId, string Url);

    private static ResolvedTarget? ResolveInternalHref(
        string href,
        string currentPageId,
        IReadOnlySet<string> knownPageIds,
        IReadOnlyDictionary<string, PageAnchors> anchorIndex)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return null;

        var hashIndex = href.IndexOf('#');
        var filePart = hashIndex >= 0 ? href[..hashIndex] : href;
        var originalAnchor = hashIndex >= 0 ? href[(hashIndex + 1)..] : "";

        var pageId = ResolvePageId(currentPageId, filePart);
        if (pageId is null || !knownPageIds.Contains(pageId))
            return null;

        if (originalAnchor.Length == 0)
            return new ResolvedTarget(pageId, $"{pageId}.html");

        if (!anchorIndex.TryGetValue(pageId, out var pageAnchors) ||
            !pageAnchors.SlugsByAnchorName.TryGetValue(originalAnchor, out var slug))
            return null;

        // Same-page as a bare "#slug" (Terminal.Gui's Markdown view auto-scrolls to these on its
        // own, scoped to whichever page is currently rendered - never ambiguous across pages, since
        // Markdig computes ids fresh per render), cross-page as "pageId.html#slug", which
        // DocViewerShell.LinkClicked resolves the same way it already does for the cc65 manuals.
        var url = pageId == currentPageId ? $"#{slug}" : $"{pageId}.html#{slug}";
        return new ResolvedTarget(pageId, url);
    }

    /// <summary>Resolves a possibly-relative href (<c>pointers.html</c>, <c>../chapter6/</c>,
    /// <c>/c_book/copyright.html</c>) against <paramref name="currentPageId"/>'s own directory
    /// (e.g. <c>chapter5</c> for <c>chapter5/pointers</c>) into a book-relative page id
    /// (<c>chapter5/pointers</c>) - or null if it resolves to a directory rather than a
    /// <c>.html</c> page (every excluded chapter/preface/answers index page - see this class's
    /// own doc comment).</summary>
    private static string? ResolvePageId(string currentPageId, string filePart)
    {
        if (filePart.Length == 0)
            return currentPageId;

        var path = filePart.StartsWith('/')
            ? filePart // already book-root-relative once the leading "/c_book/" (if any) is stripped below.
            : (currentPageId.Contains('/') ? currentPageId[..currentPageId.LastIndexOf('/')] : "") + "/" + filePart;

        if (path.StartsWith("/c_book/", StringComparison.OrdinalIgnoreCase))
            path = path["/c_book/".Length..];
        else if (path.StartsWith('/'))
            path = path[1..];

        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }

        if (segments.Count == 0)
            return null;

        var last = segments[^1];
        if (!last.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            return null; // a directory (index page) link - not an ingested page.

        segments[^1] = last[..^".html".Length];
        return string.Join('/', segments);
    }

    private static void AppendHeading(Context ctx, HtmlNode node)
    {
        var level = node.Name.Equals("h1", StringComparison.OrdinalIgnoreCase) ? 1
            : node.Name.Equals("h2", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
        var text = FlattenText(node);
        if (text.Length == 0)
            return;

        EnsureBlankLine(ctx.Sb);
        ctx.Sb.Append(new string('#', level)).Append(' ').Append(text).Append('\n');
        EnsureBlankLine(ctx.Sb);
    }

    private static void AppendListItems(Context ctx, HtmlNode list, bool ordered)
    {
        var index = 1;
        foreach (var item in list.ChildNodes.Where(n => string.Equals(n.Name, "li", StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Sb.Append(ordered ? $"{index++}. " : "- ");
            AppendChildren(item, ctx);
            ctx.Sb.Append('\n');
        }
    }

    private static void AppendDefinitionList(Context ctx, HtmlNode dl)
    {
        foreach (var child in dl.ChildNodes)
        {
            if (string.Equals(child.Name, "dt", StringComparison.OrdinalIgnoreCase))
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
            else if (string.Equals(child.Name, "dd", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Sb.Append(": ");
                AppendChildren(child, ctx);
                ctx.Sb.Append("\n\n");
            }
        }
    }

    private static void AppendTable(Context ctx, HtmlNode table)
    {
        var rows = table.Descendants("tr").ToList();
        if (rows.Count == 0)
            return;

        var columnCount = rows.Max(r => r.ChildNodes.Count(n => n.Name is "td" or "th"));
        var isFirstRow = true;
        foreach (var row in rows)
        {
            var cells = row.ChildNodes
                .Where(n => n.Name is "td" or "th")
                .Select(n => CollapseWhitespace(HtmlEntity.DeEntitize(n.InnerText)).Trim().Replace("|", "\\|"))
                .ToList();
            ctx.Sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (isFirstRow)
            {
                ctx.Sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columnCount))).Append('\n');
                isFirstRow = false;
            }
        }
    }

    private static void AppendInlineText(Context ctx, string text, bool leadingSpace)
    {
        if (ctx.Sb.Length > 0 && leadingSpace && ctx.Sb[^1] != '\n' && ctx.Sb[^1] != ' ')
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
