using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Modal dialog collecting a new name for an existing file, renamed in place (no directory
/// picker - same folder the file already lives in). Mirrors <see cref="NewFileDialog"/>'s layout.
/// </summary>
public sealed class RenameFileDialog : Dialog
{
    private readonly TextField _nameField;
    private readonly string _currentFileName;

    /// <summary>The entered filename, or null if the dialog was cancelled or the name wasn't
    /// actually changed.</summary>
    public string? NewFileName { get; private set; }

    public RenameFileDialog(string currentFileName)
    {
        _currentFileName = currentFileName;

        Title = "Rename File";
        Width = 64;
        Height = 15;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);

        var currentLabel = new Label { Text = $"Renaming: {currentFileName}", X = 0, Y = 0, Width = Dim.Fill() };
        var nameLabel = new Label { Text = "New name:", X = 0, Y = 2 };
        // Y = 4, not 3: a blank row between the label and its field, same as every other field
        // in the app - see the "every field needs clearance on all 4 sides" convention.
        _nameField = new TextField { X = 0, Y = 4, Width = Dim.Fill(1), Text = currentFileName };

        // The primary action: Accent-scheme so it visually pops against the dialog's normal
        // chrome, the same accent color the app uses for the menu bar's own highlighted items.
        var renameButton = new Button { Text = "_Rename", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        renameButton.Accepting += (_, e) =>
        {
            var newName = _nameField.Text.Trim();
            // Blank or unchanged (e.g. the user just hit Enter without editing anything) isn't an
            // error, but it isn't a rename either - leave NewFileName null either way, same as Cancel.
            if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, _currentFileName, StringComparison.Ordinal))
                NewFileName = newName;
            Application.RequestStop(this);
            // Without this, the unhandled Accept command bubbles up and the Dialog's default
            // handling closes it - harmless here since Save/Cancel both stop the dialog anyway,
            // but kept for consistency with every other dialog's Accepting handler.
            e.Handled = true;
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        cancelButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([currentLabel, nameLabel, _nameField, renameButton, cancelButton]);
    }
}
