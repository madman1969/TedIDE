using System.Collections.ObjectModel;
using Tedide.Core;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Modal dialog for choosing a loaded project's cc65 compiler optimization preset
/// (<see cref="Cc65OptimizationLevel"/>). On "Save", writes the choice directly onto the given
/// <see cref="TedideProject"/> and persists it to the .tproj file - <see cref="Cc65Toolchain.BuildArguments"/>
/// picks it up on the next build.
/// </summary>
public sealed class OptimizerSettingsDialog : Dialog
{
    private readonly DropDownList _levelField;

    /// <summary>True if the user chose Save (and the project was updated and saved to disk).</summary>
    public bool Saved { get; private set; }

    public OptimizerSettingsDialog(TedideProject project)
    {
        Title = $"Optimizer Settings - {project.Name}";
        Width = 72;
        Height = 16;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);

        var levelLabel = new Label { Text = "Optimization level:", X = 0, Y = 0 };
        _levelField = new DropDownList
        {
            X = 0, Y = 1, Width = Dim.Fill(),
            Source = new ListWrapper<string>(new ObservableCollection<string>(
                Enum.GetValues<Cc65OptimizationLevel>().Select(l => l.DisplayName()))),
            Text = project.OptimizationLevel.DisplayName(),
        };

        var helpLabel = new Label
        {
            Text = "-O      Optimize code\n" +
                   "-Oi     Optimize code, inline functions (increases code size)\n" +
                   "-Or     Optimize code, honor the register keyword\n" +
                   "-Os     Optimize code, inline some known functions\n" +
                   "-Ox     Optimize code, extended optimizations\n" +
                   "-Oirs   Combines -Oi, -Or and -Os (most aggressive setting)",
            X = 0, Y = 3, Width = Dim.Fill(), Height = 6,
        };

        // The primary action: Accent-scheme so it visually pops against the dialog's normal
        // chrome, the same accent color the app uses for the menu bar's own highlighted items.
        var saveButton = new Button { Text = "_Save", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        saveButton.Accepting += (_, e) =>
        {
            if (!Cc65OptimizationLevelExtensions.TryParse(_levelField.Text, out var level))
            {
                MessageBox.ErrorQuery(Application.Instance, "Invalid optimization level", $"'{_levelField.Text}' is not a known optimization level.", ["OK"]);
                e.Handled = true;
                return;
            }

            project.OptimizationLevel = level;
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

        Add([levelLabel, _levelField, helpLabel, saveButton, cancelButton]);
    }
}
