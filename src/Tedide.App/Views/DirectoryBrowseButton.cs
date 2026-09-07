using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Builds a "Browse" button that opens a directory-only picker (<see cref="OpenDialog"/> in
/// <see cref="OpenMode.Directory"/> mode) and writes the chosen path back into
/// <paramref name="targetField"/> on selection. Originally New Project's Directory field's own
/// inline logic, factored out here once Project Settings' CC65/VICE tabs needed the identical
/// mechanism - same button size/position (10 wide, anchored 11 from the right edge, leaving a
/// 1-cell gap before it and after it to the field's container edge) so every directory field in
/// the app looks and behaves the same.
/// </summary>
internal static class DirectoryBrowseButton
{
    /// <summary>
    /// Creates the button, wired to <paramref name="targetField"/>. Caller positions it via
    /// <paramref name="y"/> on the same row as the field, which should itself be narrowed to
    /// <c>Dim.Fill(12)</c> to leave room for the button - and adds both the field and button to
    /// its container.
    /// </summary>
    public static Button Create(TextField targetField, int y)
    {
        var button = new Button { Text = "_Browse", X = Pos.AnchorEnd(11), Y = y, Width = 10 };
        button.Accepting += (_, e) =>
        {
            var dialog = new OpenDialog
            {
                Title = "Select Directory",
                OpenMode = OpenMode.Directory,
                AllowsMultipleSelection = false,
                Path = NearestExistingDirectory(targetField.Text),
            };
            Application.Run(dialog);
            if (dialog.FilePaths.FirstOrDefault() is { } path)
                targetField.Text = path;
            e.Handled = true;
        };
        return button;
    }

    /// <summary>
    /// Walks up from <paramref name="path"/> to the nearest ancestor that actually exists, since
    /// the field's current value is often a directory that doesn't exist yet (a new project's
    /// not-yet-created folder, an unconfigured toolchain path) - passing that straight to
    /// OpenDialog.Path would start the browser somewhere that doesn't exist.
    /// </summary>
    private static string NearestExistingDirectory(string path)
    {
        var dir = path;
        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            dir = Path.GetDirectoryName(dir) ?? "";
        return string.IsNullOrEmpty(dir) ? Environment.CurrentDirectory : dir;
    }
}
