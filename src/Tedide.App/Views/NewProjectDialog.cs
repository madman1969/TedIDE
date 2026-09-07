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
        Height = 23;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);

        // Every field below sits 2 rows under its own label (a blank row between them) and is at
        // least 2 rows above whatever follows it (likewise) - see the "every field needs
        // clearance on all 4 sides" convention, rather than the label/field pairs sitting
        // directly adjacent with no breathing room.
        var nameLabel = new Label { Text = "Name:", X = 0, Y = 0 };
        _nameField = new TextField { X = 0, Y = 2, Width = Dim.Fill(1), Text = "NewGame" };

        var dirLabel = new Label { Text = "Directory:", X = 0, Y = 4 };
        _directoryField = new TextField
        {
            X = 0, Y = 6, Width = Dim.Fill(1),
            Text = Path.Combine(System.Environment.CurrentDirectory, "NewGame"),
        };

        var targetLabel = new Label { Text = "Target:", X = 0, Y = 8 };
        _targetField = new TextField { X = 0, Y = 10, Width = Dim.Fill(1), Text = "c64" };

        // The full cl65 target list is too long for one line (it silently overflowed the dialog
        // width here before this wrap), so it gets its own wrapped help block below the field
        // instead of being crammed into the label - WrapText is generous with height so this
        // keeps working if cc65 grows more targets later.
        var validTargets = string.Join(", ", Enum.GetValues<Cc65Target>().Select(t => t.ToCl65Id()));
        var targetHelpLabel = new Label
        {
            Text = WrapText($"Valid values: {validTargets}", 52),
            X = 0, Y = 12, Width = Dim.Fill(1), Height = 4,
        };

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

        Add([nameLabel, _nameField, dirLabel, _directoryField, targetLabel, _targetField, targetHelpLabel, createButton, cancelButton]);
    }

    /// <summary>Greedy word-wraps <paramref name="text"/> to lines no longer than <paramref name="width"/> characters, breaking only on spaces.</summary>
    private static string WrapText(string text, int width)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (candidate.Length > width && current.Length > 0)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0)
            lines.Add(current);
        return string.Join('\n', lines);
    }
}
