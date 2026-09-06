using System.Collections.ObjectModel;
using Tedide.Core;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
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
        Height = 19;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);

        var nameLabel = new Label { Text = "Name:", X = 0, Y = 0 };
        _nameField = new TextField { X = 0, Y = 1, Width = Dim.Fill(), Text = project.Name };

        // Restricted to Commodore hardware - see Cc65TargetExtensions.CommodoreTargets. If the
        // project's current target falls outside that list (e.g. set by hand-editing the .tproj,
        // or before this restriction existed), it's still shown here so Save doesn't silently
        // change it - it just won't appear in the dropdown's own options.
        var targetLabel = new Label { Text = "Target (Commodore only):", X = 0, Y = 3 };
        _targetField = new DropDownList
        {
            X = 0, Y = 4, Width = Dim.Fill(),
            Source = new ListWrapper<string>(new ObservableCollection<string>(
                Cc65TargetExtensions.CommodoreTargets.Select(t => t.ToCl65Id()))),
            Text = project.Target.ToCl65Id(),
        };

        var outputLabel = new Label { Text = $"Output file (blank = {project.Name}{project.Target.DefaultOutputExtension()}):", X = 0, Y = 6 };
        _outputFileField = new TextField { X = 0, Y = 7, Width = Dim.Fill(), Text = project.OutputFile ?? string.Empty };

        var extraArgsLabel = new Label { Text = "Extra cl65 arguments:", X = 0, Y = 9 };
        _extraArgumentsField = new TextField { X = 0, Y = 10, Width = Dim.Fill(), Text = string.Join(' ', project.ExtraArguments) };

        var infoLabel = new Label
        {
            Text = $"{project.SourceFiles.Count} source file(s) in {project.Directory}",
            X = 0, Y = 12, Width = Dim.Fill(),
        };

        // The primary action: Accent-scheme so it visually pops against the dialog's normal
        // chrome, the same accent color the app uses for the menu bar's own highlighted items.
        var saveButton = new Button { Text = "_Save", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
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
            e.Handled = true;
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        cancelButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([nameLabel, _nameField, targetLabel, _targetField, outputLabel, _outputFileField,
            extraArgsLabel, _extraArgumentsField, infoLabel, saveButton, cancelButton]);
    }
}
