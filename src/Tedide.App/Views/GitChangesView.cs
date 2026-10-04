using System.Collections.ObjectModel;
using Tedide.Git;
using Tedide.Theming;
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
/// Amend last commit makes the commit buttons replace the last commit; Stashes... opens
/// <see cref="StashesDialog"/>. Branches (beside the branch name) opens <see cref="BranchesDialog"/>, History
/// <see cref="HistoryDialog"/>. While a merge, rebase, cherry-pick or revert is stopped on
/// conflicts, Enter on a conflicted file opens <see cref="ConflictDialog"/> and the commit buttons
/// become Continue and Abort.
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
    private readonly Button _commitButton;
    private readonly Button _commitAllButton;
    private GitOperation _operation;
    private readonly CheckBox _amendBox;
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

    /// <summary>The message, whether to stage everything first (Commit All), and whether to amend
    /// the last commit instead of making a new one.</summary>
    public event Action<string, bool, bool>? CommitRequested;

    /// <summary>Amend last commit ticked (true) or cleared - so the host can load or drop the last
    /// commit's message.</summary>
    public event Action<bool>? AmendToggled;

    /// <summary>The Stashes button: put changes away, or bring them back.</summary>
    public event Action? StashesRequested;

    public event Action? RefreshRequested;

    /// <summary>The Branches button, beside the branch name: switch, create or delete branches.</summary>
    public event Action? BranchesRequested;

    /// <summary>The History button: the repository's commits.</summary>
    public event Action? HistoryRequested;

    /// <summary>H on a file: that file's commits.</summary>
    public event Action<GitFileStatus>? FileHistoryRequested;

    /// <summary>Enter on a conflicted file while a merge, rebase, cherry-pick or revert is stopped.</summary>
    public event Action<GitFileStatus>? ConflictRequested;

    /// <summary>Continue the stopped operation (the commit message, for a merge).</summary>
    public event Action<GitOperation, string>? ContinueRequested;

    public event Action<GitOperation>? AbortRequested;

    public event Action? FetchRequested;
    public event Action? PullRequested;
    public event Action? PushRequested;

    /// <summary>Cancel, while a fetch, pull or push is running.</summary>
    public event Action? CancelRequested;

    public GitChangesView()
    {
        CanFocus = true;
        var noHotKey = TerminalGuiWorkarounds.NoHotKey;
        const int leftWidth = 40;

        var branchesButton = Button("Branches", 0, 0, () => BranchesRequested?.Invoke());
        var historyButton = Button("History", Pos.Right(branchesButton) + 2, 0, () => HistoryRequested?.Invoke());
        _branchLabel = new Label { X = Pos.Right(historyButton) + 2, Y = 0, Width = Dim.Percent(leftWidth) - 27, HotKeySpecifier = noHotKey };
        var messageLabel = new Label { Text = "Commit message:", X = 0, Y = 2 };
        _amendBox = new CheckBox { Text = "Amend last commit", X = Pos.Right(messageLabel) + 4, Y = 2, HotKeySpecifier = noHotKey };
        _amendBox.ValueChanged += (_, _) => AmendToggled?.Invoke(IsAmending);
        // Tab moves on rather than typing a tab - a commit message has no use for one, and otherwise
        // the buttons and file lists after it can't be reached from the keyboard.
        _messageField = new TextView { X = 0, Y = 3, Width = Dim.Percent(leftWidth), Height = 5, BorderStyle = LineStyle.Single, TabKeyAddsTab = false };

        // While a merge, rebase, cherry-pick or revert is stopped these become Continue and Abort -
        // finishing it is what commits (see UpdateState).
        var commitButton = _commitButton = Button("Commit Staged", 0, 9, () => Commit(stageAll: false));
        var commitAllButton = _commitAllButton = Button("Commit All", Pos.Right(commitButton) + 2, 9, () => Commit(stageAll: true));
        var stageAllButton = Button("Stage All", 0, 11, () => StageRequested?.Invoke(_changes));
        var unstageAllButton = Button("Unstage All", Pos.Right(stageAllButton) + 2, 11, () => UnstageRequested?.Invoke(_staged));
        var stashesButton = Button("Stashes...", Pos.Right(unstageAllButton) + 2, 11, () => StashesRequested?.Invoke());
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

        _actions.AddRange([branchesButton, historyButton, _messageField, commitButton, commitAllButton, stageAllButton, unstageAllButton, stashesButton, fetchButton, pullButton, pushButton]);
        // Switching branches mid-pull would be trouble - one git operation on the branch at a time.
        _syncButtons.AddRange([branchesButton, stashesButton, fetchButton, pullButton, pushButton]);
        Add([branchesButton, historyButton, _branchLabel, messageLabel, _amendBox, _messageField, commitButton, commitAllButton, stageAllButton, unstageAllButton, stashesButton,
            fetchButton, pullButton, pushButton, _refreshButton, _cancelButton, _changesList, _stagedList]);
        SetStatus(null, null);
    }

    private static Button Button(string text, Pos x, int y, Action action)
    {
        // No hotkeys: Alt+letters already belong to the menu bar and the pane's own tabs.
        var button = new Button { Text = text, X = x, Y = y, HotKeySpecifier = TerminalGuiWorkarounds.NoHotKey };
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
        list.KeystrokeNavigator = TerminalGuiWorkarounds.NoTypeToSearch;
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
            else if (key == Key.H && !file.IsUntracked)
            {
                FileHistoryRequested?.Invoke(file);
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
            if (list.SelectedItem is { } index && index >= 0 && index < items().Count)
            {
                var file = items()[index];
                if (file.IsConflicted)
                    ConflictRequested?.Invoke(file);
                else if (File.Exists(file.Path))
                    OpenRequested?.Invoke(file.Path);
            }
            e.Handled = true;
        };
    }

    private void Commit(bool stageAll)
    {
        if (_operation == GitOperation.None)
            CommitRequested?.Invoke(_messageField.Text, stageAll, IsAmending);
        else if (stageAll)
            AbortRequested?.Invoke(_operation);
        else
            ContinueRequested?.Invoke(_operation, _messageField.Text);
    }

    /// <summary>After a commit: an empty message, and Amend cleared.</summary>
    public void ClearMessage()
    {
        _messageField.Text = "";
        _amendBox.Value = CheckState.UnChecked;
    }

    public bool IsAmending => _amendBox.Value == CheckState.Checked;

    public string Message
    {
        get => _messageField.Text;
        set => _messageField.Text = value;
    }

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

        var shown = $"{repository?.Root}|{status?.Describe()}|{status?.Operation}|{string.Join('\n', changeRows)}|{string.Join('\n', stagedRows)}";
        if (shown == _shown)
            return;
        _shown = shown;
        _changes = changes;
        _staged = staged;

        _operation = status?.Operation ?? GitOperation.None;
        var conflicts = changes.Count(f => f.IsConflicted);
        _branchText = repository is null || status is null
            ? "Not in a git repository."
            : _operation != GitOperation.None
                ? $"{status.Describe()}: {_operation.Describe()} stopped, " + conflicts switch
                {
                    0 => "resolved - Continue to finish",
                    1 => "1 conflict",
                    _ => $"{conflicts} conflicts",
                }
                : $"{status.Describe()}  ({Path.GetFileName(repository.Root)})";
        _hasRepository = status is not null;
        UpdateState();

        Fill(_changesList, changeRows, $"Changes ({changeRows.Count}) - Space stage, Enter open, D diff, B blame, H history, Del discard");
        Fill(_stagedList, stagedRows, $"Staged ({stagedRows.Count}) - Space unstage, Enter open, D diff, B blame, H history");
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
        var name = _operation.Describe() is { Length: > 0 } operation ? char.ToUpperInvariant(operation[0]) + operation[1..] : "";
        _commitButton.Text = _operation == GitOperation.None ? "Commit Staged" : $"Continue {name}";
        // Amending mid-merge or mid-rebase would rewrite the wrong commit.
        _amendBox.Enabled = _hasRepository && _operation == GitOperation.None;
        if (_operation != GitOperation.None)
            _amendBox.Value = CheckState.UnChecked;
        _commitAllButton.Text = _operation == GitOperation.None ? "Commit All" : $"Abort {name}";
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
