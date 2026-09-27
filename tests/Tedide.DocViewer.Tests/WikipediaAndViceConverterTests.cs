using Cc65DocsDbBuilder;

namespace Tedide.DocViewer.Tests;

/// <summary>
/// The Wikipedia articles and VICE manual chapters bundled in the Doc Viewer: each converted to
/// Markdown the viewer can render and link through, with the attribution their licences ask for.
/// </summary>
public class WikipediaAndViceConverterTests
{
    private static readonly Dictionary<string, MediaWikiHtmlToMarkdownConverter.PageAnchors> WikipediaAnchors = new()
    {
        ["wikipedia/Commodore_PET"] = new() { SlugsByAnchorName = new() { ["History"] = "history" } },
        ["wikipedia/GPIB"] = new() { SlugsByAnchorName = new() },
    };

    private static string WikipediaArticle(string body) => $"""
        <html><head><title>Commodore PET</title></head><body>
        <div class="mw-content-ltr mw-parser-output" lang="en">{body}</div>
        </body></html>
        """;

    [Fact]
    public void Wikipedia_DropsCitationSections_AndNavigationClutter()
    {
        var markdown = MediaWikiHtmlToMarkdownConverter.Convert(WikipediaArticle("""
            <div class="shortdescription nomobile noexcerpt noprint">Line of personal computers</div>
            <div role="note" class="hatnote">For other uses, see PET.</div>
            <p>The PET is a computer.</p>
            <div class="mw-heading mw-heading2"><h2 id="History">History</h2><span class="mw-editsection">[edit]</span></div>
            <p>Released in 1977.</p>
            <div class="mw-heading mw-heading2"><h2 id="References">References</h2></div>
            <div class="reflist"><ol class="references"><li>A citation</li></ol></div>
            <div class="mw-heading mw-heading3"><h3 id="Sources">Sources</h3></div>
            <p>A book.</p>
            <div class="mw-heading mw-heading2"><h2 id="External_links">External links</h2></div>
            <ul><li>Some website</li></ul>
            <div class="navbox">Commodore computers</div>
            """), "wikipedia/Commodore_PET", WikipediaAnchors, WikipediaPageCatalog.Site);

        Assert.Contains("The PET is a computer.", markdown);
        Assert.Contains("\n## History\n", markdown);
        Assert.Contains("Released in 1977.", markdown);
        foreach (var gone in new[] { "Line of personal", "For other uses", "References", "A citation", "A book", "External links", "Some website", "Commodore computers", "[edit]" })
            Assert.DoesNotContain(gone, markdown);
    }

    [Theory]
    [InlineData("/wiki/Commodore_pet", "wikipedia/Commodore_PET.html")]
    [InlineData("/wiki/Commodore_Pet", "wikipedia/Commodore_PET.html")] // two redirects differing only in case
    [InlineData("/wiki/Commodore_PET#History", "wikipedia/Commodore_PET.html#history")]
    [InlineData("/wiki/COMMODORE_PET", null)]                        // MediaWiki names are case-sensitive
    [InlineData("/wiki/IEEE-488", null)]                              // this page itself, no section: plain text
    public void Wikipedia_ResolvesRedirectsCaseSensitively(string href, string? expected)
    {
        // Resolved from the GPIB article, so the PET links are cross-page.
        Assert.Equal(expected, MediaWikiHtmlToMarkdownConverter.ResolveHref(href, "wikipedia/GPIB", WikipediaAnchors, WikipediaPageCatalog.Site));
    }

    private const string ViceChapter = """
        <HTML><HEAD><TITLE>VICE Manual - 13 Binary monitor</TITLE></HEAD><BODY>
        Go to the <A HREF="vice_1.html">first</A>, <A HREF="vice_12.html">previous</A> section.
        <P><HR><P>
        <H1><A NAME="SEC345" HREF="vice_toc.html#TOC345">13 Binary monitor</A></H1>
        <P>See section <A HREF="vice_6.html#SEC137">6.17 Monitor settings</A> and <A HREF="vice_13.html#SEC349">13.4 Commands</A>.
        <H2><A NAME="SEC346" HREF="vice_toc.html#TOC346">13.1 Command Structure</A></H2>
        <DL COMPACT>
        <DT><STRONG>byte 0: 0x02 (STX)</STRONG>
        <DD>
        <DT><STRONG>byte 1: API version ID</STRONG>
        <DD>The API version identifies incompatible changes.
        </DL>
        <H2><A NAME="SEC349" HREF="vice_toc.html#TOC349">13.4 Commands</A></H2>
        <P>Commands follow.
        <P><HR><P>
        Go to the <A HREF="vice_1.html">first</A>, <A HREF="vice_14.html">next</A> section.
        </BODY></HTML>
        """;

    [Fact]
    public void Vice_KeepsOnlyTheChapterBetweenItsNavigationRules()
    {
        var anchors = ViceManualHtmlToMarkdownConverter.BuildAnchorIndex(new Dictionary<string, string> { ["vice/binary_monitor"] = ViceChapter });
        var markdown = ViceManualHtmlToMarkdownConverter.Convert(ViceChapter, "vice/binary_monitor", anchors);

        Assert.StartsWith("# 13 Binary monitor\n", markdown);
        Assert.Contains("\n## 13.1 Command Structure\n", markdown);
        Assert.DoesNotContain("Go to the", markdown);
        // Same-chapter section links resolve; a link into an unbundled chapter keeps its text only.
        Assert.Contains("[13.4 Commands](#134-commands)", markdown);
        Assert.Contains("See section 6.17 Monitor settings and", markdown);
        // Option lists: one bold term per line, even where the term was already <STRONG>.
        Assert.Contains("**byte 0: 0x02 (STX)**\n", markdown);
        Assert.Contains("**byte 1: API version ID**\nThe API version identifies incompatible changes.", markdown);
        Assert.DoesNotContain("****", markdown);
    }

    [Theory]
    [InlineData("vice_13.html#SEC346", "vice/binary_monitor.html#131-command-structure")]
    [InlineData("vice_7.html", "vice/machine_specific.html")]
    [InlineData("vice_6.html#SEC137", null)]  // Settings and resources isn't bundled
    [InlineData("vice_toc.html#TOC345", null)]
    public void Vice_ResolvesLinksIntoBundledChapters(string href, string? expected)
    {
        var anchors = ViceManualHtmlToMarkdownConverter.BuildAnchorIndex(new Dictionary<string, string> { ["vice/binary_monitor"] = ViceChapter });

        Assert.Equal(expected, ViceManualHtmlToMarkdownConverter.ResolveHref(href, "vice/about", anchors));
    }

    [Fact]
    public void DocsDb_HasTheWikipediaAndViceBooks_WithTheirLicences()
    {
        using var database = new DocDatabase(DocsDbPath());
        var books = database.LoadCatalog().ToDictionary(b => b.Name);
        IEnumerable<string> PageIds(string book) => books[book].Categories.SelectMany(c => c.Entries).Select(e => e.FileName);

        Assert.Equal(WikipediaPageCatalog.AllFileNames.Order(), PageIds("Wikipedia").Order());
        Assert.Equal(ViceManualPageCatalog.AllFileNames.Order(), PageIds("VICE Manual").Order());
        Assert.Contains("c64wiki/PET_2001", PageIds("C64-Wiki"));

        var wikipediaLicence = database.GetMarkdown(WikipediaPageCatalog.LicencePageId);
        Assert.Contains("https://creativecommons.org/licenses/by-sa/4.0/", wikipediaLicence);
        Assert.All(WikipediaPageCatalog.ArticlePageIds, id =>
            Assert.Contains($"https://en.wikipedia.org/wiki/{id[WikipediaPageCatalog.PageIdPrefix.Length..]}", wikipediaLicence));

        Assert.Contains("GNU General Public License", database.GetMarkdown("vice/copyright"));
        Assert.Contains("GNU GENERAL PUBLIC LICENSE", database.GetMarkdown("vice/gpl"));
        Assert.Contains("PET-specific commands and settings", database.GetMarkdown("vice/machine_specific"));
    }

    private static string DocsDbPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Docs.db");
        return File.Exists(beside)
            ? beside
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tedide.DocViewer", "Docs.db"));
    }
}
