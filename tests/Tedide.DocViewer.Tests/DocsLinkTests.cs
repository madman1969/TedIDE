using System.Reflection;
using System.Text.RegularExpressions;
using Cc65DocsDbBuilder;
using Terminal.Gui.Views;

namespace Tedide.DocViewer.Tests;

/// <summary>
/// Guards the Doc Viewer's cross-references. The docs builder writes "#anchor" links using its own
/// MarkdownSlug, but the anchors that exist are the ones Terminal.Gui's Markdown view generates
/// when it renders a page - if the two drift apart, clicking a link silently goes nowhere. They had
/// drifted: 711 of Docs.db's 3,745 internal links (mostly underscore names like "_DE_ISDIR") pointed
/// at anchors the view never generated. These tests check against the view's own (internal)
/// GenerateAnchorSlug, so a future Terminal.Gui change to it fails here rather than in use.
/// </summary>
public class DocsLinkTests
{
    private static readonly Func<string, string> ViewSlug = (Func<string, string>)Delegate.CreateDelegate(
        typeof(Func<string, string>),
        typeof(Markdown).GetMethod("GenerateAnchorSlug", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Terminal.Gui's Markdown.GenerateAnchorSlug no longer exists - re-check how the view builds heading anchors."));

    [Theory]
    [InlineData("3.1 _DE_ISDIR")]
    [InlineData("1.32 tgi_load_vectorfont")]
    [InlineData("8. Appendix A -- example.grc")]
    [InlineData("2.2 Command line options in detail")]
    [InlineData("  Leading and trailing  ")]
    [InlineData("5.5. Sizeof and storage allocation")]
    [InlineData("#pragma warn (name, [push,] on|off)")]
    [InlineData("C++-style `comments` & <angle> \"quotes\"")]
    public void BuilderSlug_MatchesTheViewersOwnAnchor(string heading)
    {
        Assert.Equal(ViewSlug(heading), MarkdownSlug.Slugify(heading));
    }

    [Theory]
    [InlineData("3.1 _DE_ISDIR")]
    [InlineData("8. Appendix A -- example.grc")]
    [InlineData("#pragma warn (name, [push,] on|off)")]
    public void FindOnPageSlug_MatchesTheViewersOwnAnchor(string heading)
    {
        Assert.Equal(ViewSlug(heading), PageFindDialog.Slug(heading));
    }

    [Fact]
    public void EveryInternalLinkInDocsDb_PointsAtAPageAndHeadingThatExist()
    {
        using var database = new DocDatabase(DocsDbPath());
        var pages = database.LoadCatalog()
            .SelectMany(book => book.Categories)
            .SelectMany(category => category.Entries)
            .ToDictionary(entry => entry.FileName, entry => database.GetMarkdown(entry.FileName));
        var anchorsByPage = pages.ToDictionary(p => p.Key, p => HeadingAnchors(p.Value));

        var broken = new List<string>();
        var total = 0;
        foreach (var (page, markdown) in pages)
        {
            foreach (Match link in Regex.Matches(markdown, @"\]\(([^)\s]+)\)"))
            {
                var target = link.Groups[1].Value;
                if (Regex.IsMatch(target, "^[a-z]+:", RegexOptions.IgnoreCase))
                    continue; // http:, mailto: - not ours to check.
                total++;

                var hash = target.IndexOf('#');
                var file = hash >= 0 ? target[..hash] : target;
                var anchor = hash >= 0 ? target[(hash + 1)..] : null;
                var targetPage = file.Length == 0 ? page : file.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? file[..^5] : file;

                if (!anchorsByPage.TryGetValue(targetPage, out var anchors) || (anchor is not null && !anchors.Contains(anchor)))
                    broken.Add($"{page} -> {target}");
            }
        }

        Assert.True(total > 1000, $"Only {total} internal links found - is Docs.db complete?");
        Assert.True(broken.Count == 0, $"{broken.Count} of {total} links go nowhere, e.g.: {string.Join(", ", broken.Take(10))}");
    }

    /// <summary>The anchors the view generates for a page: every ATX heading outside a fenced code
    /// block, slugged by the view's own function, repeats suffixed "-1", "-2", ...</summary>
    private static HashSet<string> HeadingAnchors(string markdown)
    {
        var anchors = new HashSet<string>();
        var counts = new Dictionary<string, int>();
        var inFence = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            var heading = Regex.Match(line, @"^#{1,6} (.+?)\s*#*\s*$");
            if (inFence || !heading.Success)
                continue;
            var slug = ViewSlug(heading.Groups[1].Value);
            var count = counts.GetValueOrDefault(slug);
            counts[slug] = count + 1;
            anchors.Add(count == 0 ? slug : $"{slug}-{count}");
        }
        return anchors;
    }

    private static string DocsDbPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Docs.db");
        return File.Exists(beside)
            ? beside
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tedide.DocViewer", "Docs.db"));
    }
}
