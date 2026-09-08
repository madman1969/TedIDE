using System.Text.Json;

namespace Tedide.App;

/// <summary>
/// Persists the most-recently-opened .tproj/.tsln paths across runs, backing the File menu's
/// "Recent Projects and Solutions" list - the same feature Visual Studio offers under that name.
/// Most-recent-first, capped at <see cref="MaxEntries"/>; opening (or creating) a project/solution
/// moves it to the front, adding it if it's new. This is a per-user preference like
/// <see cref="Tedide.Theming.ThemeSettings"/>, not project state, so it lives under the OS's per-user
/// application data folder rather than any .tproj/.tsln file.
/// </summary>
public sealed class RecentProjectsSettings
{
    public const int MaxEntries = 10;

    public List<string> Paths { get; set; } = [];

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tedide", "recent.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads the previously saved list, or an empty one if none has been saved yet or the file
    /// can't be read - a missing/corrupt settings file should never stop the app from starting.
    /// </summary>
    public static RecentProjectsSettings Load()
    {
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<RecentProjectsSettings>(json, JsonOptions) ?? new RecentProjectsSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new RecentProjectsSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Moves <paramref name="path"/> to the front of the list (adding it if it's new), trims to <see cref="MaxEntries"/>, and saves.</summary>
    public void Touch(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Paths.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
        Paths.Insert(0, fullPath);
        if (Paths.Count > MaxEntries)
            Paths.RemoveRange(MaxEntries, Paths.Count - MaxEntries);
        Save();
    }

    /// <summary>Drops a path that failed to open (e.g. deleted/moved on disk) from the list, and saves.</summary>
    public void Remove(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Paths.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase)) > 0)
            Save();
    }

    /// <summary>
    /// Drops every path that no longer exists on disk (moved/deleted since it was recorded),
    /// saving only if anything was actually removed. Unlike <see cref="Remove"/> (a single path,
    /// dropped after the user tried and failed to open it), this proactively cleans the whole list
    /// - called before the "Recent Projects and Solutions" submenu is built, so a stale entry never
    /// shows up in the first place instead of only being pruned after a failed click.
    /// </summary>
    public void PruneMissing()
    {
        if (Paths.RemoveAll(p => !File.Exists(p)) > 0)
            Save();
    }
}
