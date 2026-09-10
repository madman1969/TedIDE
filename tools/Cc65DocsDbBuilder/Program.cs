using Cc65DocsDbBuilder;

// Run from this project's own directory (e.g. `dotnet run` from tools/Cc65DocsDbBuilder) - reads
// SourceHtml/*.html (the cc65 manuals) and SourceHtml/CBook/**/*.html (The C Book) and writes
// Docs.db straight into src/Tedide.DocViewer/, where it's picked up as loose content next to the
// built exe. Re-run manually whenever either source tree is updated (e.g. a refreshed download
// from https://cc65.github.io/doc/ or https://publications.gbdirect.co.uk/c_book/) - this is a
// one-time dev-time conversion, not part of the normal solution build.
var sourceHtmlDir = Path.Combine(AppContext.BaseDirectory, "SourceHtml");
var outputPath = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tedide.DocViewer", "Docs.db"));

var pages = new List<ConvertedPage>();
pages.AddRange(BuildCc65Pages(sourceHtmlDir));
pages.AddRange(BuildCBookPages(Path.Combine(sourceHtmlDir, "CBook")));

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

/// <summary>Turns an absolute source file path under <c>SourceHtml/CBook/</c> into the book-relative
/// page id <see cref="CBookPageCatalog"/> and <see cref="CBookHtmlToMarkdownConverter"/> both key on
/// (e.g. <c>.../CBook/chapter5/pointers.html</c> -&gt; <c>chapter5/pointers</c>), using forward
/// slashes regardless of OS.</summary>
static string ToPageId(string cBookSourceHtmlDir, string filePath)
{
    var relative = Path.GetRelativePath(cBookSourceHtmlDir, filePath).Replace('\\', '/');
    return relative[..^".html".Length];
}
