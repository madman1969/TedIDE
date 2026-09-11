using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Builds a "Browse" button that opens a single-file picker (<see cref="OpenDialog"/> in
/// <see cref="OpenMode.File"/> mode) and writes the chosen path back into
/// <paramref name="targetField"/> on selection - the file-picking sibling of
/// <see cref="DirectoryBrowseButton"/>, same button size/position so every browse field in the app
/// looks and behaves the same.
/// </summary>
internal static class FileBrowseButton
{
    /// <summary>
    /// Creates the button, wired to <paramref name="targetField"/>. Caller positions it via
    /// <paramref name="y"/> on the same row as the field, which should itself be narrowed to
    /// <c>Dim.Fill(12)</c> to leave room for the button - and adds both the field and button to
    /// its container.
    /// </summary>
    /// <param name="initialPath">
    /// Where the dialog should start - a specific file to pre-select (e.g. the linker config the
    /// current project's target would use by default), or just a directory. Falls back to
    /// whichever of its own ancestor directories actually exists if <paramref name="initialPath"/>
    /// itself doesn't (e.g. an unresolved CC65_HOME), and to the dialog's own default (wherever
    /// Terminal.Gui starts it) if none of them do, or if this is omitted entirely.
    /// </param>
    public static Button Create(TextField targetField, int y, string title, string? initialPath = null, params string[] extensions)
    {
        var button = new Button { Text = "_Browse", X = Pos.AnchorEnd(11), Y = y, Width = 10 };
        button.Accepting += (_, e) =>
        {
            var dialog = new OpenDialog
            {
                Title = title,
                OpenMode = OpenMode.File,
                AllowsMultipleSelection = false,
                AllowedTypes = extensions.Length > 0 ? [new AllowedType(title, extensions)] : [],
            };
            if (NearestExistingPathOrDirectory(initialPath) is { } startPath)
                dialog.Path = startPath;
            Application.Run(dialog);
            if (dialog.FilePaths.FirstOrDefault() is { } path)
                targetField.Text = path;
            e.Handled = true;
        };
        return button;
    }

    /// <summary>
    /// Walks up from <paramref name="path"/> to the nearest ancestor (itself, if it's a file that
    /// exists) that actually exists - same reasoning as <see cref="DirectoryBrowseButton"/>'s own
    /// NearestExistingDirectory, but starting from a file path rather than only ever a directory,
    /// since a pre-selected file is exactly what this is for. Returns null (leave OpenDialog.Path
    /// unset, at its own default) if given null/empty, or if nothing along the way exists.
    /// </summary>
    internal static string? NearestExistingPathOrDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        if (File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            dir = Path.GetDirectoryName(dir) ?? "";
        return string.IsNullOrEmpty(dir) ? null : dir;
    }
}
