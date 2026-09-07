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

    /// <param name="targetDirectory">The folder the new file is created directly in.</param>
    /// <param name="defaultFileName">Pre-fills the file name field with this - e.g. "newfile.h"
    /// when creating a file in an "include" folder, "newfile.c" in a "src" folder or anywhere
    /// else (see <see cref="AppShell.NewFile"/>).</param>
    public NewFileDialog(string targetDirectory, string defaultFileName = "newfile.c")
    {
        Title = "New File";
        Width = 64;
        Height = 15;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        // Dialogs default to Movable|Resizable in Terminal.Gui (see ViewArrangement) - fixed size
        // here since there's nothing in this static, fixed-position layout that benefits from
        // resizing.
        Arrangement &= ~ViewArrangement.Resizable;

        var dirLabel = new Label { Text = $"In: {targetDirectory}", X = 0, Y = 0, Width = Dim.Fill() };
        var nameLabel = new Label { Text = "File name:", X = 0, Y = 2 };
        // Y = 4, not 3: a blank row between the label and its field, same as every other field
        // in the app - see the "every field needs clearance on all 4 sides" convention.
        _nameField = new TextField { X = 0, Y = 4, Width = Dim.Fill(1), Text = defaultFileName };

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
