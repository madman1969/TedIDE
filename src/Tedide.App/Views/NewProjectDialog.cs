using Tedide.Core;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Modal dialog collecting the fields needed to scaffold a new cc65 project:
/// name, target platform and destination directory.
/// </summary>
public sealed class NewProjectDialog : Dialog
{
    private readonly TextField _nameField;
    private readonly TextField _directoryField;
    private readonly TextField _targetField;

    public string ProjectName => _nameField.Text;
    public string Directory => _directoryField.Text;
    public Cc65Target? Target { get; private set; }

    public NewProjectDialog()
    {
        Title = "New Project";
        Width = 60;
        Height = 14;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);

        var nameLabel = new Label { Text = "Name:", X = 0, Y = 0 };
        _nameField = new TextField { X = 0, Y = 1, Width = Dim.Fill(), Text = "NewGame" };

        var dirLabel = new Label { Text = "Directory:", X = 0, Y = 3 };
        _directoryField = new TextField
        {
            X = 0, Y = 4, Width = Dim.Fill(),
            Text = Path.Combine(System.Environment.CurrentDirectory, "NewGame"),
        };

        var validTargets = string.Join(", ", Enum.GetValues<Cc65Target>().Select(t => t.ToCl65Id()));
        var targetLabel = new Label { Text = $"Target ({validTargets}):", X = 0, Y = 6 };
        _targetField = new TextField { X = 0, Y = 7, Width = Dim.Fill(), Text = "c64" };

        // The primary action: Accent-scheme so it visually pops against the dialog's normal
        // chrome, the same accent color the app uses for the menu bar's own highlighted items.
        var createButton = new Button { Text = "_Create", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        createButton.Accepting += (_, e) =>
        {
            if (!Cc65TargetExtensions.TryParse(_targetField.Text, out var target))
            {
                MessageBox.ErrorQuery(Application.Instance, "Invalid target", $"'{_targetField.Text}' is not a known cc65 target.", ["OK"]);
                e.Handled = true;
                return;
            }
            Target = target;
            Application.RequestStop(this);
            e.Handled = true;
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        cancelButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([nameLabel, _nameField, dirLabel, _directoryField, targetLabel, _targetField, createButton, cancelButton]);
    }
}
