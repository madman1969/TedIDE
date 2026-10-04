using Tedide.Core;

namespace Tedide.DocViewer;

/// <summary>
/// Persists the Doc Viewer's layout across runs: the draggable Contents/Documentation divider's
/// position, as the Documentation pane's percentage of the window's width so it still makes sense
/// after resizing the terminal, and which books are collapsed in the Contents tree - the same way <c>Tedide.App.LayoutSettings</c> persists the IDE's splitters, in
/// its own file (<c>docviewer-layout.json</c>) so the two apps' settings don't collide.
/// </summary>
public sealed class DocViewerLayoutSettings
{
    public const int MinPercent = 10;
    public const int MaxPercent = 90;

    /// <summary>The Documentation pane's width, as a percentage of the window's width (the
    /// Contents tree gets the rest).</summary>
    public int ContentPaneWidthPercent { get; set; } = 70;

    /// <summary>The names of the books collapsed in the Contents tree; every other book starts
    /// expanded.</summary>
    public List<string> CollapsedBooks { get; set; } = [];

    /// <summary><see cref="ContentPaneWidthPercent"/> kept within bounds that leave both panes
    /// usable, whatever a hand-edited settings file says.</summary>
    public int ClampedContentPaneWidthPercent => Math.Clamp(ContentPaneWidthPercent, MinPercent, MaxPercent);

    public static readonly string DefaultFilePath = JsonFile.UserSettingsPath("docviewer-layout.json");

    /// <summary>
    /// Loads the previously saved divider position, or the default (70%, matching the first-run
    /// layout) if none has been saved yet or the file can't be read - a missing/corrupt settings
    /// file should never stop the app from starting.
    /// </summary>
    public static DocViewerLayoutSettings Load(string? filePath = null) => JsonFile.ReadOrDefault(filePath ?? DefaultFilePath, DocViewerJsonContext.Default.DocViewerLayoutSettings);

    public void Save(string? filePath = null) => JsonFile.Write(filePath ?? DefaultFilePath, this, DocViewerJsonContext.Default.DocViewerLayoutSettings);
}
