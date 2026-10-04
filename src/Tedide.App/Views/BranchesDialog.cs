using System.Collections.ObjectModel;
using Tedide.Git;
using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>What the user chose in <see cref="BranchesDialog"/>.</summary>
public enum BranchAction
{
    Switch,
    Create,
    Delete,
}

/// <summary>
/// The Git tab's Branches: the local branches (the current one marked), then remote ones nothing
/// local tracks yet. Enter or Switch checks the selected one out - a remote one as a new local
/// branch tracking it; Delete (or the Delete key) deletes a merged local branch; Create makes a new
/// branch from the current commit and switches to it. It only asks - AppShell runs git and closes
/// nothing on failure; <see cref="Choice"/> says what was chosen.
/// </summary>
public sealed class BranchesDialog : Dialog
{
    private readonly IReadOnlyList<GitBranch> _branches;
    private readonly ListView _list;
    private readonly TextField _nameField;

    public (BranchAction Action, GitBranch? Branch, string? NewName)? Choice { get; internal set; }

    /// <param name="currentBranch">The branch HEAD is on, or null when it's detached.</param>
    public BranchesDialog(IReadOnlyList<GitBranch> branches, string? currentBranch)
    {
        Title = "Branches";
        Width = 72;
        Height = Math.Clamp(branches.Count + 17, 20, 32);
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;
        _branches = branches;
        var noHotKey = TerminalGuiWorkarounds.NoHotKey;

        var currentLabel = new Label
        {
            Text = currentBranch is null ? "HEAD is detached - not on any branch." : $"Current branch: {currentBranch}",
            X = 0, Y = 0, Width = Dim.Fill(1), HotKeySpecifier = noHotKey,
        };

        // Draws its own border rather than sitting in a FrameView, so Tab can still reach it.
        _list = new ListView
        {
            Title = "Enter: switch, Del: delete",
            BorderStyle = LineStyle.Single,
            X = 0, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(6),
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
            // Rows start with a marker, so type-to-search finds nothing - and it would swallow keys.
            KeystrokeNavigator = TerminalGuiWorkarounds.NoTypeToSearch,
        };
        var nameWidth = branches.Count == 0 ? 0 : branches.Max(b => b.Name.Length);
        _list.SetSource(new ObservableCollection<string>(branches.Select(b => Row(b, nameWidth))));
        if (branches.Count > 0)
            _list.SelectedItem = Math.Max(0, branches.ToList().FindIndex(b => b.IsCurrent));
        _list.Accepting += (_, e) =>
        {
            Choose(BranchAction.Switch);
            e.Handled = true;
        };
        _list.KeyDown += (_, key) =>
        {
            if (key == Key.Delete)
            {
                Choose(BranchAction.Delete);
                key.Handled = true;
            }
        };

        var nameLabel = new Label { Text = "New branch from the current commit:", X = 0, Y = Pos.AnchorEnd(5) };
        _nameField = new TextField { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(16) };
        var createButton = new Button { Text = "C_reate", X = Pos.Right(_nameField) + 2, Y = Pos.AnchorEnd(3), Width = 12 };
        createButton.Accepting += (_, e) =>
        {
            CreateBranch();
            e.Handled = true;
        };

        var switchButton = new Button { Text = "_Switch", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 20, Y = Pos.AnchorEnd(1), Width = 12 };
        switchButton.Accepting += (_, e) =>
        {
            Choose(BranchAction.Switch);
            e.Handled = true;
        };
        var deleteButton = new Button { Text = "_Delete", X = Pos.Center() - 6, Y = Pos.AnchorEnd(1), Width = 12 };
        deleteButton.Accepting += (_, e) =>
        {
            Choose(BranchAction.Delete);
            e.Handled = true;
        };
        var closeButton = new Button { Text = "Close", X = Pos.Center() + 8, Y = Pos.AnchorEnd(1), Width = 12 };
        closeButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([currentLabel, _list, nameLabel, _nameField, createButton, switchButton, deleteButton, closeButton]);
        _list.SetFocus();
    }

    /// <summary>"● main         origin/main" - the current branch marked, then what it tracks;
    /// a remote branch says so.</summary>
    internal static string Row(GitBranch branch, int nameWidth)
    {
        var marker = branch.IsCurrent ? "●" : " ";
        var note = branch.IsRemote ? "(remote - switching makes a local copy)" : branch.Upstream ?? "";
        return $"{marker} {branch.Name.PadRight(nameWidth)}   {note}".TrimEnd();
    }

    private GitBranch? Selected =>
        _list.SelectedItem is { } index && index >= 0 && index < _branches.Count ? _branches[index] : null;

    private void Choose(BranchAction action)
    {
        if (Selected is not { } branch)
            return;
        if (action == BranchAction.Delete && (branch.IsCurrent || branch.IsRemote))
        {
            TedideMessageBox.ErrorQuery("Delete Branch", RenameSymbolDialog.Wrap(branch.IsCurrent
                ? $"{branch.Name} is the current branch. Switch to another one first."
                : $"{branch.Name} is a branch on the remote. Tedide only deletes local branches."), ["OK"]);
            return;
        }
        Choice = (action, branch, null);
        Application.RequestStop(this);
    }

    private void CreateBranch()
    {
        var name = _nameField.Text.Trim();
        if (name.Length == 0)
        {
            TedideMessageBox.ErrorQuery("New Branch", "Type a name for the new branch first.", ["OK"]);
            _nameField.SetFocus();
            return;
        }
        Choice = (BranchAction.Create, null, name);
        Application.RequestStop(this);
    }
}
