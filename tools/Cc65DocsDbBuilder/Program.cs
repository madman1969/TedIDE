using Cc65DocsDbBuilder;

// Run from this project's own directory (e.g. `dotnet run` from tools/Cc65DocsDbBuilder) - reads
// SourceHtml/*.html (copied next to the built exe) and writes Docs.db straight into
// src/Tedide.DocViewer/, where it's embedded as a resource. Re-run manually whenever SourceHtml is
// updated (e.g. a refreshed download from https://cc65.github.io/doc/) - this is a one-time dev-time
// conversion, not part of the normal solution build.
var sourceHtmlDir = Path.Combine(AppContext.BaseDirectory, "SourceHtml");
var outputPath = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tedide.DocViewer", "Docs.db"));

var htmlFiles = Directory.GetFiles(sourceHtmlDir, "*.html");
Console.WriteLine($"Found {htmlFiles.Length} source HTML files in {sourceHtmlDir}");

var htmlByFileName = htmlFiles.ToDictionary(
    f => Path.GetFileNameWithoutExtension(f)!,
    File.ReadAllText,
    StringComparer.Ordinal);

var missing = PageCatalog.AllFileNames.Where(f => !htmlByFileName.ContainsKey(f)).ToList();
if (missing.Count > 0)
    throw new InvalidOperationException($"PageCatalog references files with no SourceHtml/*.html: {string.Join(", ", missing)}");

Console.WriteLine("Building cross-page anchor index (pass 1)...");
var anchorIndex = HtmlToMarkdownConverter.BuildAnchorIndex(htmlByFileName);

Console.WriteLine("Converting pages to Markdown (pass 2)...");
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
            category.Name,
            categoryIndex,
            pageIndex,
            entry.Description,
            markdown));
    }
}

Console.WriteLine($"Writing {pages.Count} pages to {outputPath}...");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
DocsDatabaseWriter.Write(outputPath, pages);

Console.WriteLine("Done.");
