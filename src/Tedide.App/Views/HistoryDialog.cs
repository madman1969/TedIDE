using System.Collections.ObjectModel;
using System.Data;
using Tedide.Git;
using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// History: the repository's newest commits - or one file's, followed back through renames - in a
/// table (commit, author, age, subject), and below it the files the selected commit changed.
/// Enter on a commit moves to its files; Enter on a file (or Show Changes) closes the dialog with
/// <see cref="Choice"/> set, and the host shows that commit's change to it in a
/// <see cref="CompareDialog"/>, then reopens this at the same commit.
/// </summary>
public sealed class HistoryDialog : Dialog
{
    private readonly IReadOnlyList<GitCommit> _commits;
    private readonly TableView _table;
    private readonly ListView _files;
    private readonly Label _detail;
    private readonly DateTimeOffset _now = DateTimeOffset.Now;
    private GitCommit? _shown;

    public (GitCommit Commit, GitCommitFile File)? Choice { get; internal set; }

    /// <summary>The commit selected when the dialog closed - to reopen it there.</summary>
    public int SelectedIndex => SelectedRow ?? 0;

    /// <param name="title">"History - bounce" or "History - src/main.c".</param>
    /// <param name="selectedIndex">The commit to open on.</param>
    public HistoryDialog(string title, IReadOnlyList<GitCommit> commits, int selectedIndex = 0)
    {
        Title = title;
        // The title can echo a file name, and a title reads its first "_" as a hotkey marker.
        HotKeySpecifier = TerminalGuiWorkarounds.NoHotKey;
        Width = Dim.Percent(90);
        Height = Dim.Percent(85);
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;
        _commits = commits;

        // Commit subjects are free text - no "_" hotkey parsing.
        _detail = new Label { X = 0, Y = 0, Width = Dim.Fill(1), HotKeySpecifier = TerminalGuiWorkarounds.NoHotKey };

        _table = new TableView
        {
            X = 0, Y = 2, Width = Dim.Fill(1), Height = Dim.Percent(55),
            FullRowSelect = true,
            BorderStyle = LineStyle.Single,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        _table.Style.AlwaysShowHeaders = true;
        _table.Table = new DataTableSource(BuildTable());
        _table.ValueChanged += (_, _) => ShowSelectedCommit();

        // Draws its own border rather than sitting in a FrameView, so Tab can still reach it.
        _files = new ListView
        {
            Title = "Files changed - Enter: show changes",
            BorderStyle = LineStyle.Single,
            X = 0, Y = Pos.Bottom(_table) + 1, Width = Dim.Fill(1), Height = Dim.Fill(2),
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
            KeystrokeNavigator = TerminalGuiWorkarounds.NoTypeToSearch,
        };
        _table.Accepting += (_, e) =>
        {
            // On to the commit's files - or, when it changed just one, straight to it.
            if (_shown?.Files is [var only])
                Choose(only);
            else
                _files.SetFocus();
            e.Handled = true;
        };
        _files.Accepting += (_, e) =>
        {
            if (SelectedFile is { } file)
                Choose(file);
            e.Handled = true;
        };

        var showButton = new Button { Text = "_Show Changes", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 19, Y = Pos.AnchorEnd(1), Width = 18 };
        showButton.Accepting += (_, e) =>
        {
            if ((SelectedFile ?? _shown?.Files.FirstOrDefault()) is { } file)
                Choose(file);
            e.Handled = true;
        };
        var closeButton = new Button { Text = "Close", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 18 };
        closeButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([_detail, _table, _files, showButton, closeButton]);
        if (commits.Count > 0)
        {
            var row = Math.Clamp(selectedIndex, 0, commits.Count - 1);
            _table.SetSelection(0, row, false, null);
            _table.RowOffset = Math.Max(0, row - 3);
        }
        ShowSelectedCommit();
        _table.SetFocus();
    }

    private DataTable BuildTable()
    {
        var table = new DataTable();
        table.Columns.Add("Commit");
        table.Columns.Add("Author");
        table.Columns.Add("When");
        table.Columns.Add("Subject");
        foreach (var commit in _commits)
            table.Rows.Add(commit.ShortHash, commit.Author.Length <= 18 ? commit.Author : commit.Author[..17] + "…",
                GitBlameLine.Ago(_now - commit.When), commit.Subject);
        return table;
    }

    private int? SelectedRow =>
        _table.Value?.SelectedCell is { } cell && cell.Y >= 0 && cell.Y < _commits.Count ? cell.Y : null;

    private GitCommitFile? SelectedFile =>
        _shown is { } commit && _files.SelectedItem is { } index && index >= 0 && index < commit.Files.Count ? commit.Files[index] : null;

    /// <summary>The selected commit spelled out above the table, and its files below.</summary>
    private void ShowSelectedCommit()
    {
        var commit = SelectedRow is { } row ? _commits[row] : null;
        if (ReferenceEquals(commit, _shown) && commit is not null)
            return;
        _shown = commit;
        _detail.Text = commit is null
            ? "No commits."
            : $"{commit.ShortHash}  {commit.Author}, {commit.When.ToLocalTime():yyyy-MM-dd HH:mm}: {commit.Subject}";
        var rows = commit?.Files.Select(Row).ToList() ?? [];
        _files.SetSource(new ObservableCollection<string>(rows));
        if (rows.Count > 0)
            _files.SelectedItem = 0;
    }

    /// <summary>"M  src/main.c", "R  src/game.c  (from src/main.c)".</summary>
    internal static string Row(GitCommitFile file) =>
        file.OldPath is { } old ? $"{file.Status}  {file.Path}  (from {old})" : $"{file.Status}  {file.Path}";

    private void Choose(GitCommitFile file)
    {
        if (_shown is not { } commit)
            return;
        Choice = (commit, file);
        Application.RequestStop(this);
    }
}
