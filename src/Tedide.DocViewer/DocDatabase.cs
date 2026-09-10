using Microsoft.Data.Sqlite;

namespace Tedide.DocViewer;

/// <summary>One manual page - <see cref="FileName"/> is its primary key in Docs.db.</summary>
public sealed record PageEntry(string FileName, string Description);

/// <summary>A group of <see cref="PageEntry"/> shown together in <see cref="DocViewerShell"/>'s tree.</summary>
public sealed record PageCategory(string Name, IReadOnlyList<PageEntry> Entries);

/// <summary>One bundled book (the cc65 manual, The C Book, ...) and its categories, shown as its
/// own root node in <see cref="DocViewerShell"/>'s tree.</summary>
public sealed record BookNode(string Name, IReadOnlyList<PageCategory> Categories);

/// <summary>One full-text search hit - <see cref="Snippet"/> is a short excerpt with the matched
/// term(s) bracketed (see <see cref="DocDatabase.Search"/>).</summary>
public sealed record SearchResult(string FileName, string Book, string Description, string Snippet);

/// <summary>
/// Read-only access to Docs.db (built by tools/Cc65DocsDbBuilder - see that project for how the
/// bundled cc65 manuals became this database's <c>Pages</c> rows and <c>PagesFts</c> full-text index
/// in the first place). Owns the one <see cref="SqliteConnection"/> for the app's lifetime;
/// <see cref="DocViewerShell"/> disposes it on shutdown.
/// </summary>
public sealed class DocDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, string> _markdownCache = [];

    public DocDatabase(string path)
    {
        _connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        _connection.Open();
    }

    /// <summary>Every book, its categories and their pages, in the order
    /// <c>tools/Cc65DocsDbBuilder</c> wrote them.</summary>
    public IReadOnlyList<BookNode> LoadCatalog()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT Book, Category, FileName, Description FROM Pages
            ORDER BY BookSortOrder, CategorySortOrder, PageSortOrder;
            """;

        var books = new List<BookNode>();
        List<PageCategory>? currentCategories = null;
        List<PageEntry>? currentEntries = null;
        string? currentBook = null;
        string? currentCategory = null;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var book = reader.GetString(0);
            var category = reader.GetString(1);
            if (book != currentBook)
            {
                currentCategories = [];
                books.Add(new BookNode(book, currentCategories));
                currentBook = book;
                currentCategory = null; // a new book always starts a new category too, even if the name repeats.
            }
            if (category != currentCategory)
            {
                currentEntries = [];
                currentCategories!.Add(new PageCategory(category, currentEntries));
                currentCategory = category;
            }
            currentEntries!.Add(new PageEntry(reader.GetString(2), reader.GetString(3)));
        }
        return books;
    }

    /// <summary>A page's Markdown, cached after the first read - Docs.db is read-only and never
    /// changes underneath a running instance, so nothing can invalidate the cache mid-session.</summary>
    public string GetMarkdown(string fileName)
    {
        if (_markdownCache.TryGetValue(fileName, out var cached))
            return cached;

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Markdown FROM Pages WHERE FileName = $fileName;";
        command.Parameters.AddWithValue("$fileName", fileName);
        var markdown = (string?)command.ExecuteScalar()
            ?? throw new ArgumentException($"No page named '{fileName}' in Docs.db.", nameof(fileName));

        _markdownCache[fileName] = markdown;
        return markdown;
    }

    /// <summary>Full-text search over every page's description and Markdown (SQLite FTS5, with
    /// stemming - "compile" also matches "compiling", "compiled", etc. - via the porter tokenizer
    /// tools/Cc65DocsDbBuilder built the index with). Each search term is matched as a literal phrase
    /// with a trailing prefix wildcard (<c>"term"*</c>) rather than passing the raw query straight to
    /// FTS5's MATCH syntax, so punctuation a user might reasonably type (hyphens, quotes, "cl65 -o")
    /// can't be misread as FTS5 query operators or produce a syntax error.</summary>
    public IReadOnlyList<SearchResult> Search(string query, int limit = 50)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return [];

        var matchQuery = string.Join(" AND ", terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\"*"));

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.FileName, p.Book, p.Description, snippet(PagesFts, 2, '[', ']', ' ... ', 12)
            FROM PagesFts
            JOIN Pages p ON p.FileName = PagesFts.FileName
            WHERE PagesFts MATCH $query
            ORDER BY rank
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", matchQuery);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<SearchResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new SearchResult(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return results;
    }

    public void Dispose() => _connection.Dispose();
}
