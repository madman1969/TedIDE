using Terminal.Gui.App;
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
        Height = 9;

        var dirLabel = new Label { Text = $"In: {targetDirectory}", X = 1, Y = 1, Width = Dim.Fill(1) };
        var nameLabel = new Label { Text = "File name:", X = 1, Y = 3 };
        _nameField = new TextField { X = 1, Y = 4, Width = Dim.Fill(1), Text = "newfile.c" };

        var createButton = new Button { Text = "_Create", IsDefault = true, X = Pos.Center() - 10, Y = 6 };
        createButton.Accepting += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_nameField.Text))
                return;

            FileName = _nameField.Text.Trim();
            Application.RequestStop(this);
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 2, Y = 6 };
        cancelButton.Accepting += (_, _) => Application.RequestStop(this);

        Add([dirLabel, nameLabel, _nameField, createButton, cancelButton]);
    }
}
