using System.Text.RegularExpressions;

namespace Tedide.DocViewer;

/// <summary>
/// Finds the section for a word, for Tedide's F1 (<c>--topic word</c>): a heading that names it -
/// "3.96 cputsxy" in the cc65 function reference, "11.10 .BYT, .BYTE" in the ca65 manual, "3.2.2.
/// The while and do statements" in The C Book. A heading that is exactly the word beats one that
/// merely contains it; otherwise the earlier book and page win, in the catalog's own order (the
/// cc65 manual first). Fenced code is skipped, since a "#define" there isn't a heading.
/// </summary>
internal static partial class TopicLookup
{
    public sealed record Page(string FileName, string Markdown);

    /// <summary>The page and heading anchor for <paramref name="word"/>, or null if no heading names it.</summary>
    public static (string FileName, string Anchor)? Find(IEnumerable<Page> pages, string word)
    {
        word = word.Trim();
        if (word.Length == 0)
            return null;

        (string FileName, string Anchor)? best = null;
        var bestScore = 0;
        foreach (var page in pages)
        {
            foreach (var (title, slug) in Headings(page.Markdown))
            {
                var score = Score(title, word);
                if (score <= bestScore)
                    continue;
                best = (page.FileName, slug);
                bestScore = score;
                if (score == ExactScore)
                    return best;
            }
        }
        return best;
    }

    private const int ExactScore = 2;

    /// <summary>2 if the heading (less its section number) is the word, 1 if it's one of the
    /// heading's words, else 0 - case-insensitively, so ".byte" finds ".BYTE".</summary>
    internal static int Score(string heading, string word)
    {
        var name = SectionNumber().Replace(heading, "").Trim();
        if (string.Equals(name, word, StringComparison.OrdinalIgnoreCase))
            return ExactScore;
        return name.Split([' ', ',', '(', ')', '`'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => string.Equals(token, word, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

    /// <summary>Every heading's text and anchor slug, in page order - the same slugs
    /// <see cref="PageFindDialog.FindHeadingAbove"/> gives, repeated headings' "-1" suffixes included.</summary>
    internal static IEnumerable<(string Title, string Slug)> Headings(string markdown)
    {
        var counts = new Dictionary<string, int>();
        var inFence = false;
        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence || PageFindDialog.HeadingText(line) is not { } title)
                continue;
            var slug = PageFindDialog.Slug(title);
            var count = counts.GetValueOrDefault(slug);
            counts[slug] = count + 1;
            yield return (title, count == 0 ? slug : $"{slug}-{count}");
        }
    }

    /// <summary>"3.96 ", "1.3.9. ", "10 " - a heading's leading section number.</summary>
    [GeneratedRegex(@"^\d+(\.\d+)*\.?\s+")]
    private static partial Regex SectionNumber();
}
