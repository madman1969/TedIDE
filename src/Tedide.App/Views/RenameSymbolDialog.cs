using Tedide.Core.Navigation;
using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Edit > Rename Symbol's prompt: asks for the new name, checks it with the supplied planner
/// (<see cref="CodeNavigator.PlanRename"/>) and, if that refuses - an invalid or clashing name -
/// shows why and stays open for another try. Closes with <see cref="Plan"/> set only once there's a
/// workable plan; the host applies it.
/// </summary>
public sealed class RenameSymbolDialog : Dialog
{
    private readonly TextField _nameField;
    private readonly Label _errorLabel;
    private readonly Func<string, RenamePlan> _planner;

    public RenamePlan? Plan { get; internal set; }

    public RenameSymbolDialog(string symbol, int referenceCount, int fileCount, Func<string, RenamePlan> planner)
    {
        _planner = planner;

        Title = "Rename Symbol";
        // 78 columns inside the border and padding - wide enough for every line below unwrapped.
        Width = ContentWidth + 6;
        Height = 17;
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;

        // HotKeySpecifier disabled on every label that echoes a symbol: a Label reads its first "_"
        // as a hotkey marker, so "border_flash" would show as "borderflash".
        var noHotKey = TerminalGuiWorkarounds.NoHotKey;
        var nameLabel = new Label { Text = $"New name for '{symbol}':", X = 0, Y = 0, HotKeySpecifier = noHotKey };
        _nameField = new TextField { Text = symbol, X = 0, Y = 2, Width = Dim.Fill() };
        _nameField.Accepting += (_, e) =>
        {
            TryRename();
            e.Handled = true;
        };

        var summary = new Label
        {
            Text = $"{referenceCount} reference(s) in {fileCount} file(s) will change; comments and strings won't.\n"
                + "Files open in tabs change in the editor, unsaved - Undo reverts each one.\n"
                + "Files that aren't open are saved straight away.",
            X = 0,
            Y = 4,
            Width = Dim.Fill(),
            Height = 3,
        };

        _errorLabel = new Label { Text = string.Empty, X = 0, Y = 8, Width = Dim.Fill(), Height = 2, SchemeName = "Error", HotKeySpecifier = noHotKey };

        var renameButton = new Button { Text = "_Rename", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        renameButton.Accepting += (_, e) =>
        {
            TryRename();
            e.Handled = true;
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        cancelButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([nameLabel, _nameField, summary, _errorLabel, renameButton, cancelButton]);
        _nameField.SetFocus();
    }

    /// <summary>What renaming to <paramref name="name"/> would do - what the Rename button asks.</summary>
    internal RenamePlan PlanFor(string name) => _planner(name);

    private const int ContentWidth = 78;

    /// <summary>Breaks a message into lines of at most <see cref="ContentWidth"/> at spaces, for
    /// the two-line error label.</summary>
    internal static string Wrap(string message)
    {
        var lines = new List<string>();
        var line = string.Empty;
        foreach (var word in message.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > ContentWidth)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = line.Length == 0 ? word : line + " " + word;
            }
        }
        lines.Add(line);
        return string.Join('\n', lines);
    }

    private void TryRename()
    {
        var plan = _planner(_nameField.Text);
        if (plan.Error is { } error)
        {
            _errorLabel.Text = Wrap(error);
            _errorLabel.SetNeedsDraw();
            return;
        }

        Plan = plan;
        Application.RequestStop(this);
    }
}
