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
    public static Button Create(TextField targetField, int y, string title, params string[] extensions)
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
            Application.Run(dialog);
            if (dialog.FilePaths.FirstOrDefault() is { } path)
                targetField.Text = path;
            e.Handled = true;
        };
        return button;
    }
}
