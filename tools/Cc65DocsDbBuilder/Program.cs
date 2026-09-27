using Cc65DocsDbBuilder;

// Run from this project's own directory (e.g. `dotnet run` from tools/Cc65DocsDbBuilder) - reads
// SourceHtml/*.html (the cc65 manuals), SourceHtml/CBook/**/*.html (The C Book) and
// SourceHtml/C64Wiki/*.html (curated C64-Wiki articles) and writes Docs.db straight into
// src/Tedide.DocViewer/, where it's picked up as loose content next to the built exe. Re-run
// manually whenever a source tree is updated (e.g. a refreshed download from
// https://cc65.github.io/doc/, https://publications.gbdirect.co.uk/c_book/ or
// https://www.c64-wiki.com) - this is a one-time dev-time conversion, not part of the normal
// solution build.
var sourceHtmlDir = Path.Combine(AppContext.BaseDirectory, "SourceHtml");
var outputPath = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tedide.DocViewer", "Docs.db"));

var pages = new List<ConvertedPage>();
pages.AddRange(BuildCc65Pages(sourceHtmlDir));
pages.AddRange(BuildCBookPages(Path.Combine(sourceHtmlDir, "CBook")));
pages.AddRange(BuildC64WikiPages(Path.Combine(sourceHtmlDir, "C64Wiki")));

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

static List<ConvertedPage> BuildC64WikiPages(string c64WikiSourceHtmlDir)
{
    const string book = "C64-Wiki";
    const int bookSortOrder = 2;

    var htmlByPageId = C64WikiPageCatalog.ArticlePageIds.ToDictionary(
        id => id,
        id =>
        {
            var path = Path.Combine(c64WikiSourceHtmlDir, id[C64WikiPageCatalog.PageIdPrefix.Length..] + ".html");
            return File.Exists(path)
                ? File.ReadAllText(path)
                : throw new InvalidOperationException($"C64WikiPageCatalog references {id}, which has no {path}");
        },
        StringComparer.Ordinal);
    Console.WriteLine($"Found {htmlByPageId.Count} C64-Wiki articles in {c64WikiSourceHtmlDir}");

    Console.WriteLine("Building C64-Wiki cross-page anchor index (pass 1)...");
    var anchorIndex = C64WikiHtmlToMarkdownConverter.BuildAnchorIndex(htmlByPageId);

    Console.WriteLine("Converting C64-Wiki articles to Markdown (pass 2)...");
    var pages = new List<ConvertedPage>();
    for (var categoryIndex = 0; categoryIndex < C64WikiPageCatalog.Categories.Count; categoryIndex++)
    {
        var category = C64WikiPageCatalog.Categories[categoryIndex];
        for (var pageIndex = 0; pageIndex < category.Entries.Count; pageIndex++)
        {
            var entry = category.Entries[pageIndex];
            var markdown = entry.FileName == C64WikiPageCatalog.LicencePageId
                ? C64WikiLicencePage(htmlByPageId, File.ReadAllText(Path.Combine(c64WikiSourceHtmlDir, "fdl-1.3.txt")))
                : C64WikiHtmlToMarkdownConverter.Convert(htmlByPageId[entry.FileName], entry.FileName, anchorIndex);
            pages.Add(new ConvertedPage(entry.FileName, book, bookSortOrder, category.Name, categoryIndex, pageIndex, entry.Description, markdown));
        }
    }
    return pages;
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
    sb.Append("| Article | Source | Revision | Last edited |\n| --- | --- | --- | --- |\n");
    foreach (var (_, html) in htmlByPageId)
    {
        var page = C64WikiHtmlToMarkdownConverter.Meta(html, "c64wiki-page");
        var revision = C64WikiHtmlToMarkdownConverter.Meta(html, "c64wiki-revision");
        var edited = C64WikiHtmlToMarkdownConverter.Meta(html, "c64wiki-edited");
        sb.Append($"| {C64WikiHtmlToMarkdownConverter.Title(html)} | https://www.c64-wiki.com/wiki/{page} | ");
        sb.Append($"https://www.c64-wiki.com/index.php?oldid={revision} (history: https://www.c64-wiki.com/index.php?title={page}&action=history) | {edited} |\n");
    }
    sb.Append("\n## GNU Free Documentation License\n\n```\n");
    sb.Append(licenceText.Replace("\r\n", "\n").Trim('\n'));
    sb.Append("\n```\n");
    return sb.ToString();
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
