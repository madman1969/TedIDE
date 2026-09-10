using Microsoft.Data.Sqlite;

namespace Cc65DocsDbBuilder;

/// <summary>One converted page, ready to write to Docs.db.</summary>
public sealed record ConvertedPage(string FileName, string Book, int BookSortOrder, string Category, int CategorySortOrder, int PageSortOrder, string Description, string Markdown);

/// <summary>
/// Writes the converted pages into a fresh SQLite database (Docs.db) that Tedide.DocViewer embeds
/// and reads at runtime instead of holding 53 raw HTML files and re-parsing them - see
/// <see cref="HtmlToMarkdownConverter"/> for the actual conversion. Also builds a <c>PagesFts</c>
/// FTS5 index over each page's title/description/Markdown for the doc viewer's full-text search.
/// </summary>
public static class DocsDatabaseWriter
{
    public static void Write(string path, IReadOnlyList<ConvertedPage> pages)
    {
        if (File.Exists(path))
            File.Delete(path);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE Pages (
                    FileName TEXT PRIMARY KEY,
                    Book TEXT NOT NULL,
                    BookSortOrder INTEGER NOT NULL,
                    Category TEXT NOT NULL,
                    CategorySortOrder INTEGER NOT NULL,
                    PageSortOrder INTEGER NOT NULL,
                    Description TEXT NOT NULL,
                    Markdown TEXT NOT NULL
                );

                CREATE VIRTUAL TABLE PagesFts USING fts5(
                    FileName UNINDEXED,
                    Description,
                    Markdown,
                    tokenize = 'porter unicode61'
                );
                """;
            command.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using (var insertPage = connection.CreateCommand())
        {
            insertPage.CommandText =
                """
                INSERT INTO Pages (FileName, Book, BookSortOrder, Category, CategorySortOrder, PageSortOrder, Description, Markdown)
                VALUES ($fileName, $book, $bookSortOrder, $category, $categorySortOrder, $pageSortOrder, $description, $markdown);
                """;
            var fileName = insertPage.Parameters.Add("$fileName", SqliteType.Text);
            var book = insertPage.Parameters.Add("$book", SqliteType.Text);
            var bookSortOrder = insertPage.Parameters.Add("$bookSortOrder", SqliteType.Integer);
            var category = insertPage.Parameters.Add("$category", SqliteType.Text);
            var categorySortOrder = insertPage.Parameters.Add("$categorySortOrder", SqliteType.Integer);
            var pageSortOrder = insertPage.Parameters.Add("$pageSortOrder", SqliteType.Integer);
            var description = insertPage.Parameters.Add("$description", SqliteType.Text);
            var markdown = insertPage.Parameters.Add("$markdown", SqliteType.Text);

            using var insertFts = connection.CreateCommand();
            insertFts.CommandText = "INSERT INTO PagesFts (FileName, Description, Markdown) VALUES ($fileName, $description, $markdown);";
            var ftsFileName = insertFts.Parameters.Add("$fileName", SqliteType.Text);
            var ftsDescription = insertFts.Parameters.Add("$description", SqliteType.Text);
            var ftsMarkdown = insertFts.Parameters.Add("$markdown", SqliteType.Text);

            foreach (var page in pages)
            {
                fileName.Value = page.FileName;
                book.Value = page.Book;
                bookSortOrder.Value = page.BookSortOrder;
                category.Value = page.Category;
                categorySortOrder.Value = page.CategorySortOrder;
                pageSortOrder.Value = page.PageSortOrder;
                description.Value = page.Description;
                markdown.Value = page.Markdown;
                insertPage.ExecuteNonQuery();

                ftsFileName.Value = page.FileName;
                ftsDescription.Value = page.Description;
                ftsMarkdown.Value = page.Markdown;
                insertFts.ExecuteNonQuery();
            }
        }
        transaction.Commit();

        using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            vacuum.ExecuteNonQuery();
        }
    }
}
