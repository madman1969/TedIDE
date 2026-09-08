using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Cc65DocsDbBuilder;

/// <summary>
/// Converts one cc65 manual page's HTML (LinuxDoc-Tools output) to Markdown for
/// <c>Terminal.Gui.Views.Markdown</c>, resolving every internal cross-reference to a heading-anchor
/// slug that view's own (Markdig-based) auto-heading-id algorithm will independently reproduce at
/// render time - confirmed by direct testing against the actual 2.4.17 package (see the project's
/// working notes): lowercase, drop everything but letters/digits/spaces/hyphens, spaces to hyphens,
/// and - critically - suffix "-1", "-2", ... on each repeated slug in document order.
///
/// That last point is why this can't just point links at cc65's own original anchor names (like
/// "s2"): every one of these manuals defines its table of contents as a run of headings with the
/// *exact same text* as the real section headings further down the same page ("2. Usage" appears
/// as a heading twice - once in the ToC, once for real) - under Markdig's own auto-slugging, the
/// first occurrence (the ToC entry) would claim the plain slug and the second (the real section, the
/// one links should actually reach) would get bumped to "-1". Rather than replicate that ordering
/// dependency, <see cref="BuildAnchorIndex"/> only assigns slugs to *real* section headings (detected
/// by cc65's own consistent "tocN"/"tocN.M" vs "sN"/"ssN.M" anchor-naming convention - see
/// <see cref="IsTocAnchor"/>) and <see cref="Convert"/> renders a ToC heading as a plain Markdown
/// list item (a link, not a heading) instead of a second real heading - so only one heading per
/// section ever exists in the emitted Markdown, and slugs never collide in the first place.
/// </summary>
public static class HtmlToMarkdownConverter
{
    /// <summary>A page's real (non-ToC) heading anchors, mapped to the exact slug Terminal.Gui's
    /// Markdown view will independently compute for that heading at render time.</summary>
    public sealed class PageAnchors
    {
        public required Dictionary<string, string> SlugsByAnchorName { get; init; }
    }

    /// <summary>
    /// Pass 1: walks every page's real (non-ToC) headings in document order, computing the exact
    /// slug Markdig's auto-identifier extension will assign each one (including its own duplicate-slug
    /// numbering - see this class's own doc comment) - needed before any page can be converted, since
    /// a cross-page link has to resolve an anchor defined on a *different* page than the one being
    /// converted.
    /// </summary>
    public static Dictionary<string, PageAnchors> BuildAnchorIndex(IReadOnlyDictionary<string, string> htmlByFileName)
    {
        var index = new Dictionary<string, PageAnchors>();
        foreach (var (fileName, html) in htmlByFileName)
        {
            var document = new HtmlDocument();
            document.LoadHtml(html);

            var slugsByAnchorName = new Dictionary<string, string>();
            var slugUseCount = new Dictionary<string, int>();
            foreach (var heading in (document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode)
                         .Descendants()
                         .Where(n => n.Name is "h1" or "h2" or "h3"))
            {
                var names = HeadingAnchorNames(heading);
                if (names.Any(IsTocAnchor))
                    continue; // a ToC entry, not a real section - see this class's own doc comment.

                var text = FlattenText(heading);
                if (text.Length == 0)
                    continue;

                var slug = MakeUniqueSlug(text, slugUseCount);
                foreach (var name in names)
                    slugsByAnchorName[name] = slug;
            }

            index[fileName] = new PageAnchors { SlugsByAnchorName = slugsByAnchorName };
        }
        return index;
    }

    /// <summary>Pass 2: renders <paramref name="html"/> as Markdown, resolving internal links via
    /// <paramref name="anchorIndex"/> (built by <see cref="BuildAnchorIndex"/> across every page
    /// first). An href to anything outside <paramref name="knownFileNames"/>, or whose anchor doesn't
    /// resolve to a known heading (e.g. a non-heading anchor - rare in these manuals, and not worth
    /// the added complexity of tracking arbitrary paragraph-level anchors), is rendered as plain text
    /// rather than a dead link.</summary>
    public static string Convert(
        string html,
        string currentFileName,
        IReadOnlyDictionary<string, PageAnchors> anchorIndex,
        IReadOnlySet<string> knownFileNames)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var sb = new StringBuilder();
        var ctx = new Context(sb, currentFileName, anchorIndex, knownFileNames);
        AppendChildren(document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode, ctx);

        var text = sb.ToString();
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
            text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        return text.Trim('\n') + "\n";
    }

    private sealed class Context(StringBuilder sb, string currentFileName, IReadOnlyDictionary<string, PageAnchors> anchorIndex, IReadOnlySet<string> knownFileNames)
    {
        public readonly StringBuilder Sb = sb;
        public readonly string CurrentFileName = currentFileName;
        public readonly IReadOnlyDictionary<string, PageAnchors> AnchorIndex = anchorIndex;
        public readonly IReadOnlySet<string> KnownFileNames = knownFileNames;

        /// <summary>Whether the most recently appended text ended in whitespace, or was
        /// whitespace-only - see the equivalent field in the old Tedide.DocViewer.DocTextConverter
        /// for the full rationale (adjacent inline elements with no space between them in the source,
        /// e.g. <c>(&lt;CODE&gt;x&lt;/CODE&gt;)</c>, must not get one invented).</summary>
        public bool PendingSpace;

        /// <summary>Set while appending a <c>&lt;CODE&gt;</c>/<c>&lt;TT&gt;</c> span's content, so
        /// <see cref="AppendInlineText"/> skips <see cref="EscapeMarkdown"/> - Markdown doesn't
        /// interpret emphasis/link syntax inside a code span in the first place, so escaping there
        /// would insert a literal backslash into the rendered code instead of protecting anything.</summary>
        public bool InCode;
    }

    private static readonly Regex TocAnchorPattern = new(@"^toc[0-9.]*$", RegexOptions.Compiled);
    private static bool IsTocAnchor(string name) => TocAnchorPattern.IsMatch(name);

    private static List<string> HeadingAnchorNames(HtmlNode heading) =>
        heading.Descendants("a")
            .Select(a => a.GetAttributeValue("name", ""))
            .Where(n => n.Length > 0)
            .ToList();

    /// <summary>Markdig's default GitHub-style auto-identifier algorithm, confirmed by direct testing
    /// against the actual Terminal.Gui.Views.Markdown view rather than assumed: lowercase, drop every
    /// character that isn't a letter, digit, space or hyphen, then turn runs of whitespace into a
    /// single hyphen (e.g. "4.1 default config file (apple2.cfg)" -> "41-default-config-file-apple2cfg").</summary>
    private static string Slugify(string headingText)
    {
        var sb = new StringBuilder();
        var lastWasSeparator = true; // suppresses a leading hyphen
        foreach (var ch in headingText.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSeparator = false;
            }
            else if ((ch == ' ' || ch == '-') && !lastWasSeparator)
            {
                sb.Append('-');
                lastWasSeparator = true;
            }
        }
        return sb.ToString().TrimEnd('-');
    }

    /// <summary>Applies Markdig's duplicate-slug suffixing ("-1", "-2", ...) so
    /// <see cref="BuildAnchorIndex"/>'s slugs match what the Markdown view computes for a page whose
    /// real headings happen to repeat text (rare now that ToC entries no longer count as headings -
    /// see this class's own doc comment - but not impossible).</summary>
    private static string MakeUniqueSlug(string headingText, Dictionary<string, int> useCountBySlug)
    {
        var baseSlug = Slugify(headingText);
        var count = useCountBySlug.GetValueOrDefault(baseSlug);
        useCountBySlug[baseSlug] = count + 1;
        return count == 0 ? baseSlug : $"{baseSlug}-{count}";
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
                // The original HTML never records a snippet's language (cc65's own docs mix C,
                // ca65 assembler and shell examples with no markup distinguishing them), so "c" is
                // a fixed default rather than a per-block guess - cc65 is fundamentally a C compiler
                // suite, and even on assembler/shell snippets, C-grammar highlighting only leaves
                // most tokens as plain text rather than mis-coloring them, so this is a safe default
                // that gives most snippets real syntax highlighting without ever making a wrong one
                // look worse than the plain, unhighlighted rendering it'd otherwise get.
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

    /// <summary>Wraps <paramref name="node"/>'s inline content in Markdown emphasis markers. Empty
    /// content (e.g. a stray <c>&lt;B&gt;&lt;/B&gt;</c>) is skipped entirely - empty
    /// <c>**</c>/<c>*</c>/<c>`</c> pairs render as literal asterisks/backticks in Markdown rather
    /// than nothing, which would look like a conversion glitch.</summary>
    private static void WrapInline(Context ctx, HtmlNode node, string open, string close)
    {
        var startLength = ctx.Sb.Length;
        ctx.Sb.Append(open);
        AppendChildren(node, ctx);
        if (ctx.Sb.Length == startLength + open.Length)
        {
            ctx.Sb.Length = startLength; // nothing was actually appended - drop the empty wrapper.
            return;
        }
        ctx.Sb.Append(close);
    }

    /// <summary>
    /// A <c>&lt;CODE&gt;</c>/<c>&lt;TT&gt;</c> normally becomes an inline code span, but a small
    /// number of these manuals nest a whole <c>&lt;PRE&gt;</c> block inside one (e.g.
    /// <c>&lt;CODE&gt;&lt;PRE&gt;...&lt;/PRE&gt;&lt;/CODE&gt;</c> in c16.html) - wrapping that in
    /// backticks too would put a stray inline-code marker directly against a fenced code block,
    /// which most Markdown renderers (including Terminal.Gui's) render as broken/mismatched. Since a
    /// fenced block already reads as "verbatim code" on its own, such a &lt;CODE&gt; is rendered by
    /// just passing its content through unwrapped instead.
    /// </summary>
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

    /// <summary>
    /// Handles both roles an <c>&lt;A&gt;</c> plays in these manuals: a <c>NAME</c> needs no action
    /// here (headings already resolved their own anchor's slug via <see cref="BuildAnchorIndex"/>,
    /// and a NAME outside a heading has no Markdown equivalent worth the complexity - see this
    /// class's own doc comment), and an internal <c>HREF</c> becomes a Markdown link whose target is
    /// the resolved heading's slug - same-page as a bare <c>#slug</c> (Terminal.Gui's Markdown view
    /// auto-scrolls to these - confirmed by direct testing), cross-page as
    /// <c>targetFile.html#slug</c> (which DocViewerShell's own LinkClicked handler resolves, since
    /// the view has no way to know how to load a different page's Markdown on its own).
    /// </summary>
    private static void AppendAnchor(Context ctx, HtmlNode node)
    {
        var target = ResolveInternalHref(node.GetAttributeValue("href", ""), ctx.CurrentFileName, ctx.KnownFileNames, ctx.AnchorIndex);
        if (target is null)
        {
            AppendChildren(node, ctx);
            return;
        }

        // A separator space a preceding sibling's whitespace called for (e.g. the space between
        // "1." and this "Overview" link in "1. Overview" - see Context.PendingSpace) belongs
        // *outside* the brackets, not as a leading space inside them ("[ Overview]") - emit it here,
        // then clear PendingSpace so AppendChildren's own leading-space check (which can't tell "at
        // the very start of this link" from "mid-sentence") doesn't also insert one just inside "[".
        if (ctx.PendingSpace && ctx.Sb.Length > 0 && ctx.Sb[^1] != '\n' && ctx.Sb[^1] != ' ')
            ctx.Sb.Append(' ');
        ctx.PendingSpace = false;

        ctx.Sb.Append('[');
        var startLength = ctx.Sb.Length;
        AppendChildren(node, ctx);
        if (ctx.Sb.Length == startLength)
            ctx.Sb.Append(target.FileName); // an empty link body (rare) still needs visible text.
        ctx.Sb.Append("](").Append(target.Url).Append(')');
    }

    private sealed record ResolvedTarget(string FileName, string Url);

    private static ResolvedTarget? ResolveInternalHref(
        string href,
        string currentFileName,
        IReadOnlySet<string> knownFileNames,
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

        var fileName = filePart.Length == 0 ? currentFileName : Path.GetFileNameWithoutExtension(filePart);
        if (!knownFileNames.Contains(fileName))
            return null;

        if (originalAnchor.Length == 0)
            return new ResolvedTarget(fileName, $"{fileName}.html");

        if (!anchorIndex.TryGetValue(fileName, out var pageAnchors) ||
            !pageAnchors.SlugsByAnchorName.TryGetValue(originalAnchor, out var slug))
            return null; // an anchor that isn't a real heading (or a ToC-only ".M" sub-anchor with no matching real heading) - not resolvable.

        var url = fileName == currentFileName ? $"#{slug}" : $"{fileName}.html#{slug}";
        return new ResolvedTarget(fileName, url);
    }

    private static void AppendHeading(Context ctx, HtmlNode node)
    {
        var isToc = HeadingAnchorNames(node).Any(IsTocAnchor);
        if (isToc)
        {
            // A ToC entry - rendered as a link, not a second heading with the same text as the real
            // section further down the page (see this class's own doc comment for why).
            EnsureBlankLine(ctx.Sb);
            ctx.Sb.Append("- ");
            AppendChildren(node, ctx);
            ctx.Sb.Append('\n');
            return;
        }

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
                    ctx.Sb.Length -= 2; // empty <DT> - drop the wrapper same as WrapInline does.
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

    /// <summary>Flattens each cell to plain text (no inline links/emphasis - tables are rare across
    /// the bundled manuals, and Markdown table cells can't contain block-level wrapping anyway).</summary>
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

    /// <summary>Escapes characters Markdown would otherwise treat as syntax (emphasis markers,
    /// link/image brackets, etc.) so literal occurrences in cc65's prose (asterisks in ASCII art,
    /// square brackets in "ar65 <operation ...> lib file|module ..." examples, and so on) render as
    /// themselves instead of being misparsed.</summary>
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
