using System.Collections.ObjectModel;
using Tedide.Core;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Modal dialog for viewing/editing a loaded project's settings: its display name, cc65 target,
/// output file override, and extra cl65 arguments. Source files aren't edited here - that's the
/// Solution Explorer's right-click New File/Delete File job (see <see cref="SolutionExplorerTree"/>).
/// On "Save", writes the changes directly onto the given <see cref="TedideProject"/> and persists
/// it to disk; the caller is responsible for refreshing anything that displays project state
/// (e.g. the Solution Explorer's "Name (target)" node text).
/// </summary>
public sealed class ProjectSettingsDialog : Dialog
{
    private readonly TextField _nameField;
    private readonly DropDownList _targetField;
    private readonly TextField _outputFileField;
    private readonly TextField _extraArgumentsField;

    /// <summary>True if the user chose Save (and the project was updated and saved to disk).</summary>
    public bool Saved { get; private set; }

    public ProjectSettingsDialog(TedideProject project)
    {
        Title = $"Project Settings - {project.Name}";
        Width = 64;
        Height = 17;

        var nameLabel = new Label { Text = "Name:", X = 1, Y = 1 };
        _nameField = new TextField { X = 1, Y = 2, Width = Dim.Fill(1), Text = project.Name };

        // Restricted to Commodore hardware - see Cc65TargetExtensions.CommodoreTargets. If the
        // project's current target falls outside that list (e.g. set by hand-editing the .tproj,
        // or before this restriction existed), it's still shown here so Save doesn't silently
        // change it - it just won't appear in the dropdown's own options.
        var targetLabel = new Label { Text = "Target (Commodore only):", X = 1, Y = 4 };
        _targetField = new DropDownList
        {
            X = 1, Y = 5, Width = Dim.Fill(1),
            Source = new ListWrapper<string>(new ObservableCollection<string>(
                Cc65TargetExtensions.CommodoreTargets.Select(t => t.ToCl65Id()))),
            Text = project.Target.ToCl65Id(),
        };

        var outputLabel = new Label { Text = $"Output file (blank = {project.Name}{project.Target.DefaultOutputExtension()}):", X = 1, Y = 7 };
        _outputFileField = new TextField { X = 1, Y = 8, Width = Dim.Fill(1), Text = project.OutputFile ?? string.Empty };

        var extraArgsLabel = new Label { Text = "Extra cl65 arguments:", X = 1, Y = 10 };
        _extraArgumentsField = new TextField { X = 1, Y = 11, Width = Dim.Fill(1), Text = string.Join(' ', project.ExtraArguments) };

        var infoLabel = new Label
        {
            Text = $"{project.SourceFiles.Count} source file(s) in {project.Directory}",
            X = 1, Y = 13, Width = Dim.Fill(1),
        };

        var saveButton = new Button { Text = "_Save", IsDefault = true, X = Pos.Center() - 10, Y = 15 };
        saveButton.Accepting += (_, e) =>
        {
            if (!Cc65TargetExtensions.TryParse(_targetField.Text, out var target))
            {
                MessageBox.ErrorQuery(Application.Instance, "Invalid target", $"'{_targetField.Text}' is not a known cc65 target.", ["OK"]);
                e.Handled = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(_nameField.Text))
            {
                MessageBox.ErrorQuery(Application.Instance, "Invalid name", "Name cannot be blank.", ["OK"]);
                e.Handled = true;
                return;
            }

            project.Name = _nameField.Text.Trim();
            project.Target = target;
            project.OutputFile = string.IsNullOrWhiteSpace(_outputFileField.Text) ? null : _outputFileField.Text.Trim();
            project.ExtraArguments = _extraArgumentsField.Text
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            project.Save();

            Saved = true;
            Application.RequestStop(this);
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 2, Y = 15 };
        cancelButton.Accepting += (_, _) => Application.RequestStop(this);

        Add([nameLabel, _nameField, targetLabel, _targetField, outputLabel, _outputFileField,
            extraArgsLabel, _extraArgumentsField, infoLabel, saveButton, cancelButton]);
    }
}
