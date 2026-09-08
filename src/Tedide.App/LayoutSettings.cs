using System.Text.Json;

namespace Tedide.App;

/// <summary>
/// Persists the two draggable splitter positions - Solution Explorer/Editor width and
/// Explorer+Editor row/Output+Error List row height - across runs, each as a percentage of the
/// window's full width/height so it still makes sense after resizing the terminal or moving to a
/// different one. This is a per-user preference like <see cref="Tedide.Theming.ThemeSettings"/>, not
/// project state, so it lives under the OS's per-user application data folder.
/// </summary>
public sealed class LayoutSettings
{
    /// <summary><see cref="AppShell"/>'s editor pane width, as a percentage of the window's width.</summary>
    public int ExplorerEditorSplitPercent { get; set; } = 75;

    /// <summary><see cref="AppShell"/>'s Solution Explorer/Editor row height, as a percentage of the window's height.</summary>
    public int TopRowHeightPercent { get; set; } = 70;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tedide", "layout.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads the previously saved splitter positions, or defaults (75%/70%, matching the app's
    /// first-run layout) if none have been saved yet or the file can't be read - a missing/corrupt
    /// settings file should never stop the app from starting.
    /// </summary>
    public static LayoutSettings Load()
    {
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<LayoutSettings>(json, JsonOptions) ?? new LayoutSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new LayoutSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
