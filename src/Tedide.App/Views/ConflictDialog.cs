using Tedide.Git;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>What the user chose for a conflicted file in <see cref="ConflictDialog"/>.</summary>
public enum ConflictChoice
{
    KeepMine,
    TakeTheirs,
    Edit,
    MarkResolved,
}

/// <summary>
/// Enter on a conflicted ("!") file in the Git tab, while a merge, rebase, cherry-pick or revert
/// is stopped: keep the user's own version of the whole file, take the incoming one, open it to
/// pick lines by hand (the sections between &lt;&lt;&lt;&lt;&lt;&lt;&lt; and &gt;&gt;&gt;&gt;&gt;&gt;&gt;),
/// or mark it resolved after doing so. "Mine" always means the user's own work - AppShell and
/// <see cref="GitRepository.ResolveWithAsync"/> deal with git swapping ours/theirs in a rebase.
/// </summary>
public sealed class ConflictDialog : Dialog
{
    public ConflictChoice? Choice { get; private set; }

    /// <param name="sections">How many conflict sections the file still has (marker lines).</param>
    public ConflictDialog(string displayPath, GitOperation operation, int sections)
    {
        Title = "Resolve Conflict";
        Width = 90;
        Height = 15;
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;
        var (mine, theirs) = operation == GitOperation.Rebase
            ? ("your commit being replayed", "the branch you're rebasing onto")
            : ("your branch", operation == GitOperation.None ? "the incoming change" : $"what the {operation.Describe()} brings in");
        var source = operation == GitOperation.None ? "an earlier git command" : $"the {operation.Describe()}";
        var state = sections switch
        {
            0 => "no conflict markers left",
            1 => "1 conflicting section",
            _ => $"{sections} conflicting sections",
        };

        var text = new Label
        {
            Text = RenameSymbolDialog.Wrap($"{displayPath} has conflicts from {source} ({state}).") + "\n\n" +
                RenameSymbolDialog.Wrap($"Keep Mine takes {mine}'s version of the whole file; Take Theirs takes {theirs}. " +
                    "Or Edit it - keep what you want between the <<<<<<< and >>>>>>> markers and delete the markers - then Mark Resolved."),
            X = 0, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(2),
            HotKeySpecifier = new System.Text.Rune(0xFFFF),
        };

        // Fixed columns, not offsets from Pos.Center(): that centres each button by its own width,
        // so the offsets overlapped them and pushed Edit off the left edge (confirmed live).
        Button Choose(string label, ConflictChoice choice, int x, int width)
        {
            var button = new Button { Text = label, X = x, Y = Pos.AnchorEnd(1), Width = width };
            button.Accepting += (_, e) =>
            {
                Choice = choice;
                Application.RequestStop(this);
                e.Handled = true;
            };
            return button;
        }
        var edit = Choose("_Edit", ConflictChoice.Edit, 1, 12);
        var keepMine = Choose("_Keep Mine", ConflictChoice.KeepMine, 15, 15);
        var takeTheirs = Choose("_Take Theirs", ConflictChoice.TakeTheirs, 32, 17);
        var resolved = Choose("_Mark Resolved", ConflictChoice.MarkResolved, 51, 19);
        var cancel = new Button { Text = "Cancel", X = 72, Y = Pos.AnchorEnd(1), Width = 11 };
        cancel.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };
        // Editing by hand is the safe default - the other choices throw one side away.
        edit.IsDefault = true;
        edit.SchemeName = "Accent";

        Add([text, edit, keepMine, takeTheirs, resolved, cancel]);
        edit.SetFocus();
    }

    /// <summary>How many conflict sections <paramref name="text"/> still has - lines starting "&lt;&lt;&lt;&lt;&lt;&lt;&lt; ".</summary>
    public static int CountSections(string text) =>
        text.Split('\n').Count(line => line.StartsWith("<<<<<<<", StringComparison.Ordinal));
}
