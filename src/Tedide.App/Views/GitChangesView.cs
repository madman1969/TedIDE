using System.Collections.ObjectModel;
using System.Text;
using Tedide.Git;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// The Git tab: the solution repository's branch, a commit message with Commit (what's staged) and
/// Commit All (everything, new files included, as Visual Studio's does), and two lists - Changes
/// and Staged. In a list, Space stages or unstages the selected file, Enter opens it, D compares it
/// with the last commit (see <see cref="CompareDialog"/>), B blames it (see <see cref="BlameDialog"/>),
/// and Delete (Changes only) discards its changes. Every control is a direct child of this view: Tab only
/// moves between peers of the same SuperView, so a list nested in a frame couldn't be reached.
/// Fetch, Pull and Push sync the branch with its remote; Cancel stops one that's running.
/// It only shows and asks - AppShell runs git (see <see cref="GitRepository"/>).
/// </summary>
public sealed class GitChangesView : View
{
    private readonly Label _branchLabel;
    private readonly TextView _messageField;
    private readonly ListView _changesList;
    private readonly ListView _stagedList;
    private readonly List<View> _actions = [];
    private readonly List<View> _syncButtons = [];
    private readonly Button _refreshButton;
    private readonly Button _cancelButton;
    private string _branchText = "";
    private string? _activity;
    private List<GitFileStatus> _changes = [];
    private List<GitFileStatus> _staged = [];
    private string _shown = "";

    public event Action<IReadOnlyList<GitFileStatus>>? StageRequested;
    public event Action<IReadOnlyList<GitFileStatus>>? UnstageRequested;
    public event Action<GitFileStatus>? DiscardRequested;
    public event Action<string>? OpenRequested;

    /// <summary>D on a file: compare it with the last commit.</summary>
    public event Action<GitFileStatus>? CompareRequested;

    /// <summary>B on a file: show who last changed each of its lines.</summary>
    public event Action<GitFileStatus>? BlameRequested;

    /// <summary>The message, and whether to stage everything first (Commit All).</summary>
    public event Action<string, bool>? CommitRequested;

    public event Action? RefreshRequested;

    public event Action? FetchRequested;
    public event Action? PullRequested;
    public event Action? PushRequested;

    /// <summary>Cancel, while a fetch, pull or push is running.</summary>
    public event Action? CancelRequested;

    public GitChangesView()
    {
        CanFocus = true;
        var noHotKey = new Rune(0xFFFF);
        const int leftWidth = 40;

        _branchLabel = new Label { X = 0, Y = 0, Width = Dim.Percent(leftWidth), HotKeySpecifier = noHotKey };
        var messageLabel = new Label { Text = "Commit message:", X = 0, Y = 2 };
        _messageField = new TextView { X = 0, Y = 3, Width = Dim.Percent(leftWidth), Height = 5, BorderStyle = LineStyle.Single };

        var commitButton = Button("Commit Staged", 0, 9, () => Commit(stageAll: false));
        var commitAllButton = Button("Commit All", Pos.Right(commitButton) + 2, 9, () => Commit(stageAll: true));
        var stageAllButton = Button("Stage All", 0, 11, () => StageRequested?.Invoke(_changes));
        var unstageAllButton = Button("Unstage All", Pos.Right(stageAllButton) + 2, 11, () => UnstageRequested?.Invoke(_staged));
        var fetchButton = Button("Fetch", 0, 13, () => FetchRequested?.Invoke());
        var pullButton = Button("Pull", Pos.Right(fetchButton) + 2, 13, () => PullRequested?.Invoke());
        var pushButton = Button("Push", Pos.Right(pullButton) + 2, 13, () => PushRequested?.Invoke());
        _refreshButton = Button("Refresh", Pos.Right(pushButton) + 2, 13, () => RefreshRequested?.Invoke());
        // Takes Refresh's place while a fetch, pull or push runs - see SetActivity.
        _cancelButton = Button("Cancel", Pos.Right(pushButton) + 2, 13, () => CancelRequested?.Invoke());
        _cancelButton.Visible = false;
        commitButton.SchemeName = "Accent";

        _changesList = new ListView
        {
            X = Pos.Percent(leftWidth) + 2, Y = 0, Width = Dim.Fill(), Height = Dim.Percent(50),
            BorderStyle = LineStyle.Single,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        _stagedList = new ListView
        {
            X = Pos.Percent(leftWidth) + 2, Y = Pos.Bottom(_changesList), Width = Dim.Fill(), Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        Wire(_changesList, () => _changes, staged: false);
        Wire(_stagedList, () => _staged, staged: true);

        _actions.AddRange([_messageField, commitButton, commitAllButton, stageAllButton, unstageAllButton, fetchButton, pullButton, pushButton]);
        _syncButtons.AddRange([fetchButton, pullButton, pushButton]);
        Add([_branchLabel, messageLabel, _messageField, commitButton, commitAllButton, stageAllButton, unstageAllButton,
            fetchButton, pullButton, pushButton, _refreshButton, _cancelButton, _changesList, _stagedList]);
        SetStatus(null, null);
    }

    private static Button Button(string text, Pos x, int y, Action action)
    {
        // No hotkeys: Alt+letters already belong to the menu bar and the pane's own tabs.
        var button = new Button { Text = text, X = x, Y = y, HotKeySpecifier = new Rune(0xFFFF) };
        button.Accepting += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return button;
    }

    private void Wire(ListView list, Func<List<GitFileStatus>> items, bool staged)
    {
        // No type-to-search: ListView's OnKeyDown runs it before the KeyDown event below, so it
        // swallowed D whenever a row matched (confirmed in a test). It's no use here anyway - every
        // row starts with its status letter.
        list.KeystrokeNavigator = null;
        list.KeyDown += (_, key) =>
        {
            if (list.SelectedItem is not { } index || index < 0 || index >= items().Count)
                return;
            var file = items()[index];
            if (key == Key.Space)
            {
                if (staged)
                    UnstageRequested?.Invoke([file]);
                else
                    StageRequested?.Invoke([file]);
                key.Handled = true;
            }
            else if (key == Key.D)
            {
                CompareRequested?.Invoke(file);
                key.Handled = true;
            }
            else if (key == Key.B && !file.IsUntracked)
            {
                BlameRequested?.Invoke(file);
                key.Handled = true;
            }
            else if (key == Key.Delete && !staged)
            {
                DiscardRequested?.Invoke(file);
                key.Handled = true;
            }
        };
        list.Accepting += (_, e) =>
        {
            if (list.SelectedItem is { } index && index >= 0 && index < items().Count && File.Exists(items()[index].Path))
                OpenRequested?.Invoke(items()[index].Path);
            e.Handled = true;
        };
    }

    private void Commit(bool stageAll) => CommitRequested?.Invoke(_messageField.Text, stageAll);

    public void ClearMessage() => _messageField.Text = "";

    /// <summary>Shows <paramref name="status"/>, or "not in a repository" when it's null. Lists are
    /// only reloaded when something changed, so a periodic refresh doesn't move the selection.</summary>
    public void SetStatus(GitRepository? repository, GitStatus? status)
    {
        var changes = status?.Files.Where(f => f.HasUnstagedChanges).ToList() ?? [];
        var staged = status?.Files.Where(f => f.IsStaged).ToList() ?? [];
        string Row(GitFileStatus file, char marker) =>
            $"{marker}  {repository!.Relative(file.Path)}{(file.OriginalPath is { } from ? $"  (from {repository.Relative(from)})" : "")}";
        var changeRows = changes.Select(f => Row(f, f.Marker)).ToList();
        var stagedRows = staged.Select(f => Row(f, f.Index)).ToList();

        var shown = $"{repository?.Root}|{status?.Describe()}|{string.Join('\n', changeRows)}|{string.Join('\n', stagedRows)}";
        if (shown == _shown)
            return;
        _shown = shown;
        _changes = changes;
        _staged = staged;

        _branchText = repository is null || status is null
            ? "Not in a git repository."
            : $"Branch: {status.Describe()}  ({Path.GetFileName(repository.Root)})";
        _hasRepository = status is not null;
        UpdateState();

        Fill(_changesList, changeRows, $"Changes ({changeRows.Count}) - Space: stage, Enter: open, D: compare, B: blame, Del: discard");
        Fill(_stagedList, stagedRows, $"Staged ({stagedRows.Count}) - Space: unstage, Enter: open, D: compare, B: blame");
    }

    private bool _hasRepository;

    /// <summary>
    /// "Pushing..." while a fetch, pull or push runs, or null when none is: the branch line says
    /// so, Fetch/Pull/Push are disabled (one at a time), and Cancel takes Refresh's place.
    /// </summary>
    public void SetActivity(string? activity)
    {
        _activity = activity;
        UpdateState();
    }

    private void UpdateState()
    {
        _branchLabel.Text = _activity is null ? _branchText : $"{_branchText}  -  {_activity}";
        foreach (var action in _actions)
            action.Enabled = _hasRepository;
        foreach (var button in _syncButtons)
            button.Enabled = _hasRepository && _activity is null;
        var focusCancel = _activity is not null && _syncButtons.Any(b => b.HasFocus);
        _cancelButton.Visible = _activity is not null;
        _refreshButton.Visible = _activity is null;
        if (focusCancel)
            _cancelButton.SetFocus();
    }

    private static void Fill(ListView list, List<string> rows, string title)
    {
        var selected = list.SelectedItem;
        list.Title = title;
        list.SetSource(new ObservableCollection<string>(rows));
        if (rows.Count > 0)
            list.SelectedItem = Math.Clamp(selected ?? 0, 0, rows.Count - 1);
    }
}
