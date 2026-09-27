using Cc65DocsDbBuilder;

// Run from this project's own directory (e.g. `dotnet run` from tools/Cc65DocsDbBuilder) - reads
// SourceHtml/*.html (the cc65 manuals), SourceHtml/CBook/**/*.html (The C Book),
// SourceHtml/C64Wiki/*.html and SourceHtml/Wikipedia/*.html (curated wiki articles) and
// SourceHtml/Vice/*.html (VICE manual chapters) and writes Docs.db straight into
// src/Tedide.DocViewer/, where it's picked up as loose content next to the built exe. Re-run
// manually whenever a source tree is updated (e.g. a refreshed download from
// https://cc65.github.io/doc/, https://publications.gbdirect.co.uk/c_book/,
// https://www.c64-wiki.com, https://en.wikipedia.org or https://vice-emu.sourceforge.io) - this is
// a one-time dev-time conversion, not part of the normal solution build.
var sourceHtmlDir = Path.Combine(AppContext.BaseDirectory, "SourceHtml");
var outputPath = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tedide.DocViewer", "Docs.db"));

var pages = new List<ConvertedPage>();
pages.AddRange(BuildCc65Pages(sourceHtmlDir));
pages.AddRange(BuildCBookPages(Path.Combine(sourceHtmlDir, "CBook")));
pages.AddRange(BuildC64WikiPages(Path.Combine(sourceHtmlDir, "C64Wiki")));
pages.AddRange(BuildWikipediaPages(Path.Combine(sourceHtmlDir, "Wikipedia")));
pages.AddRange(BuildVicePages(Path.Combine(sourceHtmlDir, "Vice")));

Console.WriteLine($"Writing {pages.Count} pages to {outputPath}...");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
DocsDatabaseWriter.Write(outputPath, pages);

Console.WriteLine("Done.");

static List<ConvertedPage> BuildCc65Pages(string sourceHtmlDir)
{
    const string book = "cc65 Manual";
    const int bookSortOrder = 0;

    var htmlFiles = Directory.GetFiles(sourceHtmlDir, "*.html");
    Console.WriteLine($"Found {htmlFiles.Length} cc65 source HTML files in {sourceHtmlDir}");

    var htmlByFileName = htmlFiles.ToDictionary(
        f => Path.GetFileNameWithoutExtension(f)!,
        File.ReadAllText,
        StringComparer.Ordinal);

    var missing = PageCatalog.AllFileNames.Where(f => !htmlByFileName.ContainsKey(f)).ToList();
    if (missing.Count > 0)
        throw new InvalidOperationException($"PageCatalog references files with no SourceHtml/*.html: {string.Join(", ", missing)}");

    Console.WriteLine("Building cc65 cross-page anchor index (pass 1)...");
    var anchorIndex = HtmlToMarkdownConverter.BuildAnchorIndex(htmlByFileName);

    Console.WriteLine("Converting cc65 pages to Markdown (pass 2)...");
    var pages = new List<ConvertedPage>();
    for (var categoryIndex = 0; categoryIndex < PageCatalog.Categories.Count; categoryIndex++)
    {
        var category = PageCatalog.Categories[categoryIndex];
        for (var pageIndex = 0; pageIndex < category.Entries.Count; pageIndex++)
        {
            var entry = category.Entries[pageIndex];
            var markdown = HtmlToMarkdownConverter.Convert(htmlByFileName[entry.FileName], entry.FileName, anchorIndex, PageCatalog.AllFileNames);
            pages.Add(new ConvertedPage(
                entry.FileName,
                book,
                bookSortOrder,
                category.Name,
                categoryIndex,
                pageIndex,
                entry.Description,
                markdown));
        }
    }
    return pages;
}

static List<ConvertedPage> BuildCBookPages(string cBookSourceHtmlDir)
{
    const string book = "The C Book";
    const int bookSortOrder = 1;

    var htmlFiles = Directory.GetFiles(cBookSourceHtmlDir, "*.html", SearchOption.AllDirectories);
    Console.WriteLine($"Found {htmlFiles.Length} C Book source HTML files in {cBookSourceHtmlDir}");

    var htmlByPageId = htmlFiles.ToDictionary(
        f => ToPageId(cBookSourceHtmlDir, f),
        File.ReadAllText,
        StringComparer.Ordinal);

    var missing = CBookPageCatalog.AllFileNames.Where(f => !htmlByPageId.ContainsKey(f)).ToList();
    if (missing.Count > 0)
        throw new InvalidOperationException($"CBookPageCatalog references files with no SourceHtml/CBook/*.html: {string.Join(", ", missing)}");

    Console.WriteLine("Building C Book cross-page anchor index (pass 1)...");
    var anchorIndex = CBookHtmlToMarkdownConverter.BuildAnchorIndex(htmlByPageId);

    Console.WriteLine("Converting C Book pages to Markdown (pass 2)...");
    var pages = new List<ConvertedPage>();
    for (var categoryIndex = 0; categoryIndex < CBookPageCatalog.Categories.Count; categoryIndex++)
    {
        var category = CBookPageCatalog.Categories[categoryIndex];
        for (var pageIndex = 0; pageIndex < category.Entries.Count; pageIndex++)
        {
            var entry = category.Entries[pageIndex];
            var markdown = CBookHtmlToMarkdownConverter.Convert(htmlByPageId[entry.FileName], entry.FileName, anchorIndex, CBookPageCatalog.AllFileNames);
            pages.Add(new ConvertedPage(
                entry.FileName,
                book,
                bookSortOrder,
                category.Name,
                categoryIndex,
                pageIndex,
                entry.Description,
                markdown));
        }
    }
    return pages;
}

static List<ConvertedPage> BuildC64WikiPages(string sourceDir) =>
    BuildMediaWikiBook("C64-Wiki", 2, sourceDir, C64WikiPageCatalog.Site, C64WikiPageCatalog.Categories,
        C64WikiPageCatalog.ArticlePageIds, C64WikiPageCatalog.LicencePageId,
        html => C64WikiLicencePage(html, File.ReadAllText(Path.Combine(sourceDir, "fdl-1.3.txt"))));

static List<ConvertedPage> BuildWikipediaPages(string sourceDir) =>
    BuildMediaWikiBook("Wikipedia", 3, sourceDir, WikipediaPageCatalog.Site, WikipediaPageCatalog.Categories,
        WikipediaPageCatalog.ArticlePageIds, WikipediaPageCatalog.LicencePageId, WikipediaLicencePage);

/// <summary>A book of MediaWiki articles (C64-Wiki or Wikipedia): each article converted by
/// <see cref="MediaWikiHtmlToMarkdownConverter"/>, plus the book's generated licence page.</summary>
static List<ConvertedPage> BuildMediaWikiBook(
    string book, int bookSortOrder, string sourceDir, MediaWikiSite site, IReadOnlyList<PageCategory> categories,
    IReadOnlyList<string> articlePageIds, string licencePageId, Func<IReadOnlyDictionary<string, string>, string> licencePage)
{
    var htmlByPageId = articlePageIds.ToDictionary(
        id => id,
        id =>
        {
            var path = Path.Combine(sourceDir, id[site.PageIdPrefix.Length..] + ".html");
            return File.Exists(path)
                ? File.ReadAllText(path)
                : throw new InvalidOperationException($"The {book} catalog references {id}, which has no {path}");
        },
        StringComparer.Ordinal);
    Console.WriteLine($"Found {htmlByPageId.Count} {book} articles in {sourceDir}");

    Console.WriteLine($"Building {book} cross-page anchor index (pass 1)...");
    var anchorIndex = MediaWikiHtmlToMarkdownConverter.BuildAnchorIndex(htmlByPageId, site);

    Console.WriteLine($"Converting {book} articles to Markdown (pass 2)...");
    var pages = new List<ConvertedPage>();
    for (var categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
    {
        var category = categories[categoryIndex];
        for (var pageIndex = 0; pageIndex < category.Entries.Count; pageIndex++)
        {
            var entry = category.Entries[pageIndex];
            var markdown = entry.FileName == licencePageId
                ? licencePage(htmlByPageId)
                : MediaWikiHtmlToMarkdownConverter.Convert(htmlByPageId[entry.FileName], entry.FileName, anchorIndex, site);
            pages.Add(new ConvertedPage(entry.FileName, book, bookSortOrder, category.Name, categoryIndex, pageIndex, entry.Description, markdown));
        }
    }
    return pages;
}

/// <summary>The sources table on a wiki licence page: each article's title, source URL, the exact
/// revision taken and its history (which lists its authors), and when it was last edited.</summary>
static string WikiSourcesTable(IReadOnlyDictionary<string, string> htmlByPageId, string articleUrlBase, string indexPhp)
{
    var sb = new System.Text.StringBuilder();
    sb.Append("| Article | Source | Revision | Last edited |\n| --- | --- | --- | --- |\n");
    foreach (var (_, html) in htmlByPageId)
    {
        var page = MediaWikiHtmlToMarkdownConverter.Meta(html, "wiki-page");
        var revision = MediaWikiHtmlToMarkdownConverter.Meta(html, "wiki-revision");
        var edited = MediaWikiHtmlToMarkdownConverter.Meta(html, "wiki-edited");
        sb.Append($"| {MediaWikiHtmlToMarkdownConverter.Title(html)} | {articleUrlBase}{page} | ");
        sb.Append($"{indexPhp}?oldid={revision} (history: {indexPhp}?title={page}&action=history) | {edited} |\n");
    }
    return sb.ToString();
}

/// <summary>
/// The C64-Wiki licence page: what the GNU FDL asks of a redistributed copy - the licence notice
/// and its full text, and for each article its title, where it came from and exactly which
/// revision (whose history on the wiki lists the article's authors).
/// </summary>
static string C64WikiLicencePage(IReadOnlyDictionary<string, string> htmlByPageId, string licenceText)
{
    var sb = new System.Text.StringBuilder();
    sb.Append("# C64-Wiki licence and sources\n\n");
    sb.Append("The articles in this section are from **C64-Wiki** (https://www.c64-wiki.com), whose content ");
    sb.Append("is available under the **GNU Free Documentation License** - the full text is below. ");
    sb.Append("They're redistributed here under that licence, and so remain under it.\n\n");
    sb.Append("Each article's text is unchanged apart from being converted to Markdown for this viewer: ");
    sb.Append("images, the wiki's contents boxes, \"edit\" links and citation markers are left out, and ");
    sb.Append("links to articles not included here keep their text but not the link.\n\n");
    sb.Append("## Sources\n\n");
    sb.Append("The authors of each article are listed in its history on C64-Wiki.\n\n");
    sb.Append(WikiSourcesTable(htmlByPageId, "https://www.c64-wiki.com/wiki/", "https://www.c64-wiki.com/index.php"));
    sb.Append("\n## GNU Free Documentation License\n\n```\n");
    sb.Append(licenceText.Replace("\r\n", "\n").Trim('\n'));
    sb.Append("\n```\n");
    return sb.ToString();
}

/// <summary>
/// The Wikipedia licence page: what CC BY-SA 4.0 asks of a redistributed copy - attribution (each
/// article's title, source, revision and history), the licence and a link to it, and a note of
/// what was changed.
/// </summary>
static string WikipediaLicencePage(IReadOnlyDictionary<string, string> htmlByPageId)
{
    var sb = new System.Text.StringBuilder();
    sb.Append("# Wikipedia licence and sources\n\n");
    sb.Append("The articles in this section are from the English **Wikipedia** (https://en.wikipedia.org), ");
    sb.Append("and are licensed under the **Creative Commons Attribution-ShareAlike 4.0 International License** ");
    sb.Append("(CC BY-SA 4.0) - https://creativecommons.org/licenses/by-sa/4.0/ (legal code: ");
    sb.Append("https://creativecommons.org/licenses/by-sa/4.0/legalcode). They're redistributed here under ");
    sb.Append("that licence, and these adapted versions are under it too.\n\n");
    sb.Append("## Changes\n\n");
    sb.Append("Each article has been converted to Markdown for this viewer. Images, the contents box, \"edit\" ");
    sb.Append("links, hatnotes, navigation boxes, maintenance banners and citation markers are left out, as are ");
    sb.Append("the References, Notes, Sources, Further reading and External links sections. Links to articles ");
    sb.Append("not included here keep their text but not the link. The text is otherwise unchanged.\n\n");
    sb.Append("## Sources\n\n");
    sb.Append("The authors of each article are listed in its history on Wikipedia.\n\n");
    sb.Append(WikiSourcesTable(htmlByPageId, "https://en.wikipedia.org/wiki/", "https://en.wikipedia.org/w/index.php"));
    return sb.ToString();
}

/// <summary>The bundled VICE manual chapters (see <see cref="ViceManualPageCatalog"/>) - its own
/// Copyright and GPL chapters included, which serve as this book's licence pages.</summary>
static List<ConvertedPage> BuildVicePages(string sourceDir)
{
    const string book = "VICE Manual";
    const int bookSortOrder = 4;

    var htmlByPageId = ViceManualPageCatalog.AllChapters.ToDictionary(
        c => c.PageId,
        c =>
        {
            var path = Path.Combine(sourceDir, $"vice_{c.Number}.html");
            return File.Exists(path)
                ? File.ReadAllText(path)
                : throw new InvalidOperationException($"ViceManualPageCatalog references chapter {c.Number}, which has no {path}");
        },
        StringComparer.Ordinal);
    Console.WriteLine($"Found {htmlByPageId.Count} VICE manual chapters in {sourceDir}");

    Console.WriteLine("Building VICE manual cross-page anchor index (pass 1)...");
    var anchorIndex = ViceManualHtmlToMarkdownConverter.BuildAnchorIndex(htmlByPageId);

    Console.WriteLine("Converting VICE manual chapters to Markdown (pass 2)...");
    var pages = new List<ConvertedPage>();
    for (var categoryIndex = 0; categoryIndex < ViceManualPageCatalog.Categories.Count; categoryIndex++)
    {
        var (category, chapters) = ViceManualPageCatalog.Categories[categoryIndex];
        for (var pageIndex = 0; pageIndex < chapters.Count; pageIndex++)
        {
            var chapter = chapters[pageIndex];
            var markdown = ViceManualHtmlToMarkdownConverter.Convert(htmlByPageId[chapter.PageId], chapter.PageId, anchorIndex);
            pages.Add(new ConvertedPage(chapter.PageId, book, bookSortOrder, category, categoryIndex, pageIndex, chapter.Description, markdown));
        }
    }
    return pages;
}

/// <summary>Turns an absolute source file path under <c>SourceHtml/CBook/</c> into the book-relative
/// page id <see cref="CBookPageCatalog"/> and <see cref="CBookHtmlToMarkdownConverter"/> both key on
/// (e.g. <c>.../CBook/chapter5/pointers.html</c> -&gt; <c>chapter5/pointers</c>), using forward
/// slashes regardless of OS.</summary>
static string ToPageId(string cBookSourceHtmlDir, string filePath)
{
    var relative = Path.GetRelativePath(cBookSourceHtmlDir, filePath).Replace('\\', '/');
    return relative[..^".html".Length];
}
