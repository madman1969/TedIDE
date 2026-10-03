using System.Collections.ObjectModel;
using Tedide.Git;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>What the user chose in <see cref="StashesDialog"/>.</summary>
public enum StashAction
{
    Stash,
    Pop,
    Apply,
    Drop,
}

/// <summary>
/// The Git tab's Stashes: the stashes, newest first, and a box to name a new one. Stash Changes
/// puts every uncommitted change away (new files included), leaving the files as last committed;
/// Pop (Enter) brings the selected stash back and deletes it, Apply brings it back and keeps it,
/// Drop (or the Delete key) deletes it. It only asks - AppShell runs git; <see cref="Choice"/>
/// says what was chosen.
/// </summary>
public sealed class StashesDialog : Dialog
{
    private readonly IReadOnlyList<GitStash> _stashes;
    private readonly ListView _list;
    private readonly TextField _messageField;

    public (StashAction Action, GitStash? Stash, string? Message)? Choice { get; private set; }

    /// <param name="changes">How many changed files there are to stash - for the hint.</param>
    public StashesDialog(IReadOnlyList<GitStash> stashes, int changes)
    {
        Title = "Stashes";
        Width = 80;
        Height = Math.Clamp(stashes.Count + 17, 20, 32);
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;
        _stashes = stashes;
        var now = DateTimeOffset.Now;

        var hint = new Label
        {
            Text = stashes.Count == 0 ? "No stashes yet." : "Enter: pop (bring back and delete), Del: drop",
            X = 0, Y = 0, Width = Dim.Fill(1),
        };

        // Draws its own border rather than sitting in a FrameView, so Tab can still reach it.
        _list = new ListView
        {
            Title = "Stashes, newest first",
            BorderStyle = LineStyle.Single,
            X = 0, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(6),
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
            KeystrokeNavigator = null,
        };
        _list.SetSource(new ObservableCollection<string>(stashes.Select(s => Row(s, now))));
        if (stashes.Count > 0)
            _list.SelectedItem = 0;
        _list.Accepting += (_, e) =>
        {
            Choose(StashAction.Pop);
            e.Handled = true;
        };
        _list.KeyDown += (_, key) =>
        {
            if (key == Key.Delete)
            {
                Choose(StashAction.Drop);
                key.Handled = true;
            }
        };

        var messageLabel = new Label
        {
            Text = changes == 0 ? "Nothing to stash - no uncommitted changes." : $"Stash {(changes == 1 ? "1 changed file" : $"{changes} changed files")}, with a message (optional):",
            X = 0, Y = Pos.AnchorEnd(5), Width = Dim.Fill(1),
        };
        _messageField = new TextField { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(22) };
        var stashButton = new Button { Text = "_Stash Changes", X = Pos.Right(_messageField) + 2, Y = Pos.AnchorEnd(3), Width = 19 };
        stashButton.Enabled = changes > 0;
        stashButton.Accepting += (_, e) =>
        {
            Choice = (StashAction.Stash, null, _messageField.Text.Trim());
            Application.RequestStop(this);
            e.Handled = true;
        };

        Button Act(string text, StashAction action, int x)
        {
            var button = new Button { Text = text, X = x, Y = Pos.AnchorEnd(1), Width = 12 };
            button.Accepting += (_, e) =>
            {
                Choose(action);
                e.Handled = true;
            };
            button.Enabled = stashes.Count > 0;
            return button;
        }
        var popButton = Act("_Pop", StashAction.Pop, 10);
        popButton.IsDefault = true;
        popButton.SchemeName = "Accent";
        var applyButton = Act("_Apply", StashAction.Apply, 24);
        var dropButton = Act("_Drop", StashAction.Drop, 38);
        var closeButton = new Button { Text = "Close", X = 52, Y = Pos.AnchorEnd(1), Width = 12 };
        closeButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([hint, _list, messageLabel, _messageField, stashButton, popButton, applyButton, dropButton, closeButton]);
        if (stashes.Count > 0)
            _list.SetFocus();
        else
            _messageField.SetFocus();
    }

    /// <summary>"stash@{0}  On main: tidy up  (2 hours ago)".</summary>
    internal static string Row(GitStash stash, DateTimeOffset now) =>
        $"{stash.Name}  {stash.Description}  ({GitBlameLine.Ago(now - stash.When)})";

    private void Choose(StashAction action)
    {
        if (_list.SelectedItem is not { } index || index < 0 || index >= _stashes.Count)
            return;
        Choice = (action, _stashes[index], null);
        Application.RequestStop(this);
    }
}
