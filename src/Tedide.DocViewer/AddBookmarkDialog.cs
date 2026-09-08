using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.DocViewer;

/// <summary>Modal dialog collecting a label for a new bookmark - mirrors Tedide.App's
/// RenameFileDialog/NewFileDialog layout convention (own Padding, fixed non-resizable size, an
/// Accent-scheme primary button).</summary>
public sealed class AddBookmarkDialog : Dialog
{
    private readonly TextField _labelField;

    /// <summary>The entered label, or null if the dialog was cancelled or left blank.</summary>
    public string? Label { get; private set; }

    public AddBookmarkDialog(string defaultLabel)
    {
        Title = "Add Bookmark";
        Width = 64;
        Height = 13;
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;

        var labelLabel = new Terminal.Gui.Views.Label { Text = "Bookmark label:", X = 0, Y = 0 };
        _labelField = new TextField { X = 0, Y = 2, Width = Dim.Fill(1), Text = defaultLabel };

        var addButton = new Button { Text = "_Add", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        addButton.Accepting += (_, e) =>
        {
            var text = _labelField.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                Label = text;
            Application.RequestStop(this);
            e.Handled = true;
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        cancelButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([labelLabel, _labelField, addButton, cancelButton]);
        _labelField.SetFocus();
    }
}
