using System.Text.Json;

namespace Tedide.DocViewer;

/// <summary>One saved location - a page, optionally a specific heading on it, under a
/// user-friendly <see cref="Label"/> shown in the Bookmarks menu.</summary>
public sealed record Bookmark(string FileName, string? Anchor, string Label);

/// <summary>
/// Persists the user's saved bookmarks across runs, the same way
/// <see cref="Tedide.Theming.ThemeSettings"/>/<c>Tedide.App.RecentProjectsSettings</c> persist their
/// own per-user preferences under the OS's per-user application data folder - a different file
/// (<c>docviewer-bookmarks.json</c>) so the two apps' settings don't collide.
/// </summary>
public sealed class DocBookmarks
{
    public List<Bookmark> Items { get; set; } = [];

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tedide", "docviewer-bookmarks.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads the previously saved bookmarks, or an empty list if none have been saved yet or the
    /// file can't be read - a missing/corrupt settings file should never stop the app from starting.
    /// </summary>
    public static DocBookmarks Load()
    {
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<DocBookmarks>(json, JsonOptions) ?? new DocBookmarks();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DocBookmarks();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public bool Contains(string fileName, string? anchor) =>
        Items.Any(b => b.FileName == fileName && b.Anchor == anchor);

    /// <summary>Adds a bookmark for this exact location if none exists yet, or removes it if one
    /// does - backs the Bookmarks menu's "Add/Remove Bookmark" toggle item.</summary>
    public void Toggle(string fileName, string? anchor, string label)
    {
        if (Items.RemoveAll(b => b.FileName == fileName && b.Anchor == anchor) == 0)
            Items.Add(new Bookmark(fileName, anchor, label));
        Save();
    }
}
