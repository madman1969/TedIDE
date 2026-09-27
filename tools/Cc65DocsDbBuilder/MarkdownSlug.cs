using System.Text.RegularExpressions;

namespace Cc65DocsDbBuilder;

/// <summary>
/// The heading-anchor ("slug") algorithm, shared by every HTML-to-Markdown converter in this
/// project (<see cref="HtmlToMarkdownConverter"/> for the cc65 manuals,
/// <see cref="CBookHtmlToMarkdownConverter"/> for The C Book) so the links they write match the
/// anchors <c>Terminal.Gui.Views.Markdown</c> computes when the Doc Viewer renders the page. It must
/// mirror that view's own GenerateAnchorSlug exactly, which in 2.4.17 is: trim, lowercase, drop
/// every character that isn't a word character (letter, digit, underscore), whitespace or hyphen,
/// then turn each space into a hyphen - individually, not collapsing runs - and trim hyphens from
/// the ends. Repeats get "-1", "-2", ... suffixes in document order.
///
/// An earlier version dropped underscores and collapsed hyphen runs instead, so about one link in
/// five (711 of 3,745 - mostly the function reference's "_DE_ISDIR"/"tgi_load_vectorfont" style
/// names) pointed at an anchor the view never generated, and clicking it went nowhere. The Doc
/// Viewer tests now check every link in Docs.db against the view's own function, so drift from a
/// future Terminal.Gui change shows up as a failing test instead.
/// </summary>
public static partial class MarkdownSlug
{
    [GeneratedRegex(@"[^\w\s-]")]
    private static partial Regex NotWordSpaceOrHyphen();

    public static string Slugify(string headingText) =>
        NotWordSpaceOrHyphen().Replace(headingText.Trim().ToLowerInvariant(), "").Replace(' ', '-').Trim('-');

    /// <summary>Applies the duplicate-slug suffixing ("-1", "-2", ...) so a page's headings that
    /// happen to repeat text get the same slugs the Markdown view computes for them.</summary>
    public static string MakeUnique(string headingText, Dictionary<string, int> useCountBySlug)
    {
        var baseSlug = Slugify(headingText);
        var count = useCountBySlug.GetValueOrDefault(baseSlug);
        useCountBySlug[baseSlug] = count + 1;
        return count == 0 ? baseSlug : $"{baseSlug}-{count}";
    }
}
