using Tedide.Core;

namespace Tedide.App;

/// <summary>
/// The editor's own on/off choices that aren't the Terminal.Gui.Editor's built-in view toggles -
/// View > Check As You Type (see <see cref="LiveErrorChecking"/>) and View > Code Completion (see
/// <see cref="CodeCompletion"/>). A per-user preference
/// like <see cref="LayoutSettings"/>, kept beside it.
/// </summary>
public sealed class EditorSettings
{
    /// <summary>Whether the shown file is checked for errors whenever typing pauses. On by default.</summary>
    public bool CheckAsYouType { get; set; } = true;

    /// <summary>Whether names are suggested as they're typed, and a call's signature shown. On by default.</summary>
    public bool CodeCompletion { get; set; } = true;

    internal static readonly string DefaultFilePath = JsonFile.UserSettingsPath("editor.json");

    /// <summary>Where this was loaded from, and is saved to.</summary>
    internal string FilePath { get; set; } = DefaultFilePath;

    /// <summary>The saved choices, or the defaults if none have been saved or the file can't be read.</summary>
    internal static EditorSettings Load(string path)
    {
        var settings = JsonFile.ReadOrDefault(path, AppJsonContext.Default.EditorSettings);
        settings.FilePath = path;
        return settings;
    }

    public void Save() => JsonFile.Write(FilePath, this, AppJsonContext.Default.EditorSettings);
}
