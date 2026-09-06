using Tedide.Core;
using Terminal.Gui.App;
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
        Height = 13;

        var nameLabel = new Label { Text = "Name:", X = 1, Y = 1 };
        _nameField = new TextField { X = 1, Y = 2, Width = Dim.Fill(1), Text = "NewGame" };

        var dirLabel = new Label { Text = "Directory:", X = 1, Y = 4 };
        _directoryField = new TextField
        {
            X = 1, Y = 5, Width = Dim.Fill(1),
            Text = Path.Combine(System.Environment.CurrentDirectory, "NewGame"),
        };

        var validTargets = string.Join(", ", Enum.GetValues<Cc65Target>().Select(t => t.ToCl65Id()));
        var targetLabel = new Label { Text = $"Target ({validTargets}):", X = 1, Y = 7 };
        _targetField = new TextField { X = 1, Y = 8, Width = Dim.Fill(1), Text = "c64" };

        var createButton = new Button { Text = "_Create", IsDefault = true, X = Pos.Center() - 10, Y = 10 };
        createButton.Accepting += (_, e) =>
        {
            if (!TryParseTarget(_targetField.Text, out var target))
            {
                MessageBox.ErrorQuery(Application.Instance, "Invalid target", $"'{_targetField.Text}' is not a known cc65 target.", ["OK"]);
                e.Handled = true;
                return;
            }
            Target = target;
            Application.RequestStop(this);
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 2, Y = 10 };
        cancelButton.Accepting += (_, _) => Application.RequestStop(this);

        Add([nameLabel, _nameField, dirLabel, _directoryField, targetLabel, _targetField, createButton, cancelButton]);
    }

    private static bool TryParseTarget(string text, out Cc65Target target)
    {
        foreach (var candidate in Enum.GetValues<Cc65Target>())
        {
            if (string.Equals(candidate.ToCl65Id(), text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                target = candidate;
                return true;
            }
        }
        target = default;
        return false;
    }
}
