using Cc65DocsDbBuilder;

namespace Tedide.DocViewer.Tests;

/// <summary>
/// The C64-Wiki articles bundled in the Doc Viewer: MediaWiki markup converted to Markdown the
/// viewer can render and link through, and the GNU FDL's attribution carried alongside them.
/// </summary>
public class C64WikiConverterTests
{
    private static string Article(string title, string body) => $"""
        <html><head><title>{title}</title><meta name="wiki-page" content="{title.Replace(' ', '_')}"></head><body>
        <div class="mw-content-ltr mw-parser-output" lang="en">{body}</div>
        </body></html>
        """;

    private static readonly Dictionary<string, MediaWikiHtmlToMarkdownConverter.PageAnchors> Anchors = new()
    {
        ["c64wiki/VIC"] = new() { SlugsByAnchorName = new() { ["Registers"] = "registers" } },
        ["c64wiki/CIA"] = new() { SlugsByAnchorName = new() { ["CIA_2"] = "cia-2" } },
        ["c64wiki/Sprite"] = new() { SlugsByAnchorName = new() },
    };

    private static string Convert(string body, string pageId = "c64wiki/Sprite") =>
        MediaWikiHtmlToMarkdownConverter.Convert(Article("Sprite", body), pageId, Anchors, C64WikiPageCatalog.Site);

    [Fact]
    public void TitleBecomesTheH1_AndSectionsKeepTheirLevel_WithoutEditLinks()
    {
        var markdown = Convert("""
            <h2><span class="mw-headline" id="Registers">Registers</span><span class="mw-editsection">[<a href="/index.php?title=Sprite&amp;action=edit">edit</a>]</span></h2>
            <h3><span class="mw-headline" id="Bits">Bits</span></h3>
            """);

        Assert.StartsWith("# Sprite\n", markdown);
        Assert.Contains("\n## Registers\n", markdown);
        Assert.Contains("\n### Bits\n", markdown);
        Assert.DoesNotContain("edit", markdown);
    }

    [Fact]
    public void NavigationAndMedia_AreLeftOut()
    {
        var markdown = Convert("""
            <div id="toc" class="toc"><div class="toctitle"><h2>Contents</h2></div><ul><li>1 Registers</li></ul></div>
            <figure><a href="/wiki/File:Sprite.png"><img src="x.png"></a><figcaption>A sprite</figcaption></figure>
            <p>Sprites are movable<sup class="reference">[1]</sup> objects.</p>
            <div class="printfooter">Retrieved from somewhere</div>
            """);

        Assert.Contains("Sprites are movable objects.", markdown);
        Assert.DoesNotContain("Contents", markdown);
        Assert.DoesNotContain("A sprite", markdown);
        Assert.DoesNotContain("[1]", markdown);
        Assert.DoesNotContain("Retrieved", markdown);
    }

    [Theory]
    [InlineData("/wiki/VIC", "c64wiki/VIC.html")]
    [InlineData("/wiki/VIC-II", "c64wiki/VIC.html")]                       // a redirect to a bundled article
    [InlineData("/wiki/VIC#Registers", "c64wiki/VIC.html#registers")]
    [InlineData("/wiki/CIA#CIA_2", "c64wiki/CIA.html#cia-2")]
    [InlineData("/wiki/VIC#No_such_section", "c64wiki/VIC.html")]          // still reaches the page
    [InlineData("/wiki/Some_other_article", null)]                          // not bundled
    [InlineData("https://www.example.com/", null)]
    [InlineData("/index.php?title=VIC&action=edit", null)]
    public void ResolveHref_LinksOnlyToBundledArticles(string href, string? expected)
    {
        Assert.Equal(expected, MediaWikiHtmlToMarkdownConverter.ResolveHref(href, "c64wiki/Sprite", Anchors, C64WikiPageCatalog.Site));
    }

    [Fact]
    public void LinksKeepTheirText_ButOnlyBundledOnesStayLinks()
    {
        var markdown = Convert("""
            <p>See the <a href="/wiki/VIC-II" class="mw-redirect">VIC-II</a>, the
            <a href="/wiki/Some_other_article">PLA</a> and a <a href="/wiki/Missing" class="new">red link</a>.</p>
            """);

        Assert.Contains("[VIC-II](c64wiki/VIC.html)", markdown);
        Assert.Contains(" PLA ", markdown);
        Assert.Contains("red link", markdown);
        Assert.DoesNotContain("Missing", markdown);
    }

    [Fact]
    public void Tables_StayAligned_AcrossRowAndColumnSpans()
    {
        // Shaped like the Kernal article's routine table.
        var markdown = Convert("""
            <table class="wikitable">
            <tr><th rowspan="2">Name</th><th colspan="2">Address</th><th rowspan="2">Function</th></tr>
            <tr><th>Hexadecimal</th><th>Decimal</th></tr>
            <tr><td>CHROUT</td><td>$FFD2</td><td>65490</td><td>Output a character</td></tr>
            </table>
            """);

        Assert.Contains("| Name | Address |  | Function |", markdown);
        Assert.Contains("|  | Hexadecimal | Decimal |  |", markdown);
        Assert.Contains("| CHROUT | $FFD2 | 65490 | Output a character |", markdown);
    }

    [Fact]
    public void LayoutTables_BecomeAList()
    {
        var markdown = Convert("""
            <table><tr><td><img src="w.png"></td><td>Wikipedia: PETSCII</td></tr></table>
            """);

        Assert.Contains("- Wikipedia: PETSCII", markdown);
        Assert.DoesNotContain("|", markdown);
    }

    [Fact]
    public void NestedLists_AreIndented()
    {
        var markdown = Convert("<ul><li>Sound<ul><li>Noise</li><li>Filters</li></ul></li><li>DMA</li></ul>");

        Assert.Contains("- Sound\n  - Noise\n  - Filters\n- DMA", markdown);
    }

    [Fact]
    public void Listings_BecomeFencedCode()
    {
        var markdown = Convert("<pre>10 POKE 53280,0\n20 GOTO 10\n</pre>");

        Assert.Contains("```\n10 POKE 53280,0\n20 GOTO 10\n```", markdown);
    }

    [Fact]
    public void DocsDb_HasTheWholeC64WikiBook_AndItsLicencePage()
    {
        using var database = new DocDatabase(DocsDbPath());
        var book = database.LoadCatalog().Single(b => b.Name == "C64-Wiki");
        var pageIds = book.Categories.SelectMany(c => c.Entries).Select(e => e.FileName).ToHashSet();

        Assert.Equal(C64WikiPageCatalog.AllFileNames.Order(), pageIds.Order());

        var licence = database.GetMarkdown(C64WikiPageCatalog.LicencePageId);
        Assert.Contains("GNU Free Documentation License", licence);
        Assert.Contains("Version 1.3, 3 November 2008", licence); // the full licence text itself
        Assert.All(C64WikiPageCatalog.ArticlePageIds, id =>
            Assert.Contains($"https://www.c64-wiki.com/wiki/{id[C64WikiPageCatalog.PageIdPrefix.Length..]}", licence));
    }

    private static string DocsDbPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Docs.db");
        return File.Exists(beside)
            ? beside
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tedide.DocViewer", "Docs.db"));
    }
}
