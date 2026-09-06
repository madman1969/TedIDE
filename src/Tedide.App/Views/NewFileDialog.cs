using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Modal dialog collecting a filename for a new source/header file, to be created directly in
/// the given target directory (no subdirectory picker - the target is fixed by whatever was
/// selected in the Solution Explorer when the dialog was opened).
/// </summary>
public sealed class NewFileDialog : Dialog
{
    private readonly TextField _nameField;

    /// <summary>The entered filename, or null if the dialog was cancelled.</summary>
    public string? FileName { get; private set; }

    public NewFileDialog(string targetDirectory)
    {
        Title = "New File";
        Width = 64;
        Height = 10;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);

        var dirLabel = new Label { Text = $"In: {targetDirectory}", X = 0, Y = 0, Width = Dim.Fill() };
        var nameLabel = new Label { Text = "File name:", X = 0, Y = 2 };
        _nameField = new TextField { X = 0, Y = 3, Width = Dim.Fill(), Text = "newfile.c" };

        // The primary action: Accent-scheme so it visually pops against the dialog's normal
        // chrome, the same accent color the app uses for the menu bar's own highlighted items.
        var createButton = new Button { Text = "_Create", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        createButton.Accepting += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(_nameField.Text))
            {
                FileName = _nameField.Text.Trim();
                Application.RequestStop(this);
            }
            // Without this, the unhandled Accept command bubbles up and the Dialog's default
            // handling closes it even on blank input - only a valid name should dismiss it.
            e.Handled = true;
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        cancelButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([dirLabel, nameLabel, _nameField, createButton, cancelButton]);
    }
}
