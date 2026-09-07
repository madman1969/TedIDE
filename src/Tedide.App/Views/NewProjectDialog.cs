using System.Collections.ObjectModel;
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
    private readonly DropDownList _targetField;

    public string ProjectName => _nameField.Text;
    public string Directory => _directoryField.Text;
    public Cc65Target? Target { get; private set; }

    public NewProjectDialog()
    {
        Title = "New Project";
        Width = 60;
        Height = 18;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        // Dialogs default to Movable|Resizable in Terminal.Gui (see ViewArrangement) - fixed size
        // here since there's nothing in this static, fixed-position layout that benefits from
        // resizing.
        Arrangement &= ~ViewArrangement.Resizable;

        // Every field below sits 2 rows under its own label (a blank row between them) and is at
        // least 2 rows above whatever follows it (likewise) - see the "every field needs
        // clearance on all 4 sides" convention, rather than the label/field pairs sitting
        // directly adjacent with no breathing room.
        var nameLabel = new Label { Text = "Name:", X = 0, Y = 0 };
        _nameField = new TextField { X = 0, Y = 2, Width = Dim.Fill(1), Text = "NewGame" };

        var dirLabel = new Label { Text = "Directory:", X = 0, Y = 4 };
        _directoryField = new TextField
        {
            X = 0, Y = 6, Width = Dim.Fill(12),
            Text = Path.Combine(System.Environment.CurrentDirectory, "NewGame"),
        };

        var browseButton = DirectoryBrowseButton.Create(_directoryField, y: 6);

        // Restricted to Commodore hardware, same as ProjectSettingsDialog's own target dropdown -
        // see Cc65TargetExtensions.CommodoreTargets.
        var targetLabel = new Label { Text = "Target (Commodore only):", X = 0, Y = 8 };
        _targetField = new DropDownList
        {
            X = 0, Y = 10, Width = Dim.Fill(1),
            Source = new ListWrapper<string>(new ObservableCollection<string>(
                Cc65TargetExtensions.CommodoreTargets.Select(t => t.ToCl65Id()))),
            Text = "c64",
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

        Add([nameLabel, _nameField, dirLabel, _directoryField, browseButton, targetLabel, _targetField, createButton, cancelButton]);
    }
}
