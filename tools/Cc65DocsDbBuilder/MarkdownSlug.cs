using System.Text;

namespace Cc65DocsDbBuilder;

/// <summary>
/// Markdig's default GitHub-style auto-identifier algorithm, shared by every HTML-to-Markdown
/// converter in this project (<see cref="HtmlToMarkdownConverter"/> for the cc65 manuals,
/// <see cref="CBookHtmlToMarkdownConverter"/> for The C Book) so each can build a heading-anchor
/// index that matches what <c>Terminal.Gui.Views.Markdown</c> independently computes at render
/// time - confirmed by direct testing against the actual 2.4.17 package: lowercase, drop every
/// character that isn't a letter, digit, space or hyphen, turn runs of whitespace/hyphens into a
/// single hyphen, and suffix "-1", "-2", ... on each repeated slug in document order.
/// </summary>
public static class MarkdownSlug
{
    public static string Slugify(string headingText)
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

    /// <summary>Applies Markdig's duplicate-slug suffixing ("-1", "-2", ...) so a page's headings
    /// that happen to repeat text get the same slugs the Markdown view computes for them.</summary>
    public static string MakeUnique(string headingText, Dictionary<string, int> useCountBySlug)
    {
        var baseSlug = Slugify(headingText);
        var count = useCountBySlug.GetValueOrDefault(baseSlug);
        useCountBySlug[baseSlug] = count + 1;
        return count == 0 ? baseSlug : $"{baseSlug}-{count}";
    }
}
