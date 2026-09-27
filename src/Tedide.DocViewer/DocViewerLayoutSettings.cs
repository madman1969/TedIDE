using System.Text.Json;

namespace Tedide.DocViewer;

/// <summary>
/// Persists the draggable Contents/Documentation divider's position across runs, as the
/// Documentation pane's percentage of the window's width so it still makes sense after resizing
/// the terminal - the same way <c>Tedide.App.LayoutSettings</c> persists the IDE's splitters, in
/// its own file (<c>docviewer-layout.json</c>) so the two apps' settings don't collide.
/// </summary>
public sealed class DocViewerLayoutSettings
{
    public const int MinPercent = 10;
    public const int MaxPercent = 90;

    /// <summary>The Documentation pane's width, as a percentage of the window's width (the
    /// Contents tree gets the rest).</summary>
    public int ContentPaneWidthPercent { get; set; } = 70;

    /// <summary><see cref="ContentPaneWidthPercent"/> kept within bounds that leave both panes
    /// usable, whatever a hand-edited settings file says.</summary>
    public int ClampedContentPaneWidthPercent => Math.Clamp(ContentPaneWidthPercent, MinPercent, MaxPercent);

    public static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tedide", "docviewer-layout.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads the previously saved divider position, or the default (70%, matching the first-run
    /// layout) if none has been saved yet or the file can't be read - a missing/corrupt settings
    /// file should never stop the app from starting.
    /// </summary>
    public static DocViewerLayoutSettings Load(string? filePath = null)
    {
        try
        {
            var json = File.ReadAllText(filePath ?? DefaultFilePath);
            return JsonSerializer.Deserialize<DocViewerLayoutSettings>(json, JsonOptions) ?? new DocViewerLayoutSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DocViewerLayoutSettings();
        }
    }

    public void Save(string? filePath = null)
    {
        filePath ??= DefaultFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
