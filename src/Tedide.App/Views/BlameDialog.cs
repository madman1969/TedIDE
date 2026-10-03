using System.Data;
using Tedide.Git;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Blame (git phase 5b): who last changed every line of a file, as Visual Studio's Annotate does -
/// each run of lines from one commit shows its short hash, author and age on its first line only,
/// lines not committed yet are green, and the selected line's full commit (hash, author, age and
/// summary) is spelled out above the table. Enter (or Go to Line) closes it with
/// <see cref="GoToLine"/> set; the host does the navigating.
/// </summary>
public sealed class BlameDialog : Dialog
{
    private static readonly Color UncommittedHue = new(0x73, 0xC9, 0x91);
    private const int AuthorWidth = 18;

    private readonly IReadOnlyList<GitBlameFileLine> _lines;
    private readonly TableView _table;
    private readonly Label _commitLabel;
    private readonly DateTimeOffset _now = DateTimeOffset.Now;

    /// <summary>The 1-based line to go to, or null if closed without one.</summary>
    public int? GoToLine { get; private set; }

    /// <param name="displayPath">The file's name as the title shows it, e.g. relative to its project.</param>
    /// <param name="selectedLine">The line to open on - the caret's.</param>
    public BlameDialog(string displayPath, IReadOnlyList<GitBlameFileLine> lines, int selectedLine)
    {
        Title = $"Blame - {displayPath}";
        // The title echoes a file name, and a title reads its first "_" as a hotkey marker.
        HotKeySpecifier = new System.Text.Rune(0xFFFF);
        Width = Dim.Percent(90);
        Height = Dim.Percent(85);
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;
        _lines = lines;

        // Commit summaries are free text - no "_" hotkey parsing.
        _commitLabel = new Label { X = 0, Y = 0, Width = Dim.Fill(1), HotKeySpecifier = new System.Text.Rune(0xFFFF) };

        _table = new TableView
        {
            X = 0, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(2),
            FullRowSelect = true,
            BorderStyle = LineStyle.Single,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        // Otherwise the column headers scroll away with the first row.
        _table.Style.AlwaysShowHeaders = true;
        _table.Style.RowColorGetter = args =>
            args.RowIndex >= 0 && args.RowIndex < _lines.Count && !_lines[args.RowIndex].Blame.IsCommitted
                ? CompareDialog.Tinted(_table.GetScheme(), UncommittedHue)
                : null;
        _table.Table = new DataTableSource(BuildTable());
        _table.ValueChanged += (_, _) => ShowSelectedCommit();
        _table.Accepting += (_, e) =>
        {
            AcceptSelection();
            e.Handled = true;
        };
        if (_lines.Count > 0)
        {
            var row = Math.Clamp(selectedLine - 1, 0, _lines.Count - 1);
            _table.SetSelection(0, row, false, null);
            _table.RowOffset = Math.Max(0, row - 5);
        }
        ShowSelectedCommit();

        var goButton = new Button { Text = "_Go to Line", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 19, Y = Pos.AnchorEnd(1), Width = 18 };
        goButton.Accepting += (_, e) =>
        {
            AcceptSelection();
            e.Handled = true;
        };

        var closeButton = new Button { Text = "Close", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 18 };
        closeButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([_commitLabel, _table, goButton, closeButton]);
        _table.SetFocus();
    }

    private DataTable BuildTable()
    {
        var table = new DataTable();
        table.Columns.Add("Line");
        table.Columns.Add("Commit");
        table.Columns.Add("Author");
        table.Columns.Add("When");
        table.Columns.Add("Code");
        string? previous = null;
        foreach (var line in _lines)
        {
            var blame = line.Blame;
            // Only the first line of a run from one commit names it, so the runs stand out.
            var first = blame.Commit != previous;
            previous = blame.Commit;
            var (commit, author, when) = !first ? ("", "", "")
                : blame.IsCommitted ? (blame.Commit[..7], Truncate(blame.Author, AuthorWidth), GitBlameLine.Ago(_now - blame.When))
                : ("-", "Not committed yet", "");
            table.Rows.Add($"{line.Line,5}", commit, author, when, line.Text.Replace("\t", "    "));
        }
        return table;
    }

    private static string Truncate(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";

    private int? SelectedRow =>
        _table.Value?.SelectedCell is { } cell && cell.Y >= 0 && cell.Y < _lines.Count ? cell.Y : null;

    /// <summary>"a1b2c3d  aross, 3 days ago: Add tabs" for the selected line, above the table.</summary>
    private void ShowSelectedCommit()
    {
        if (SelectedRow is not { } row)
        {
            _commitLabel.Text = _lines.Count == 0 ? "Nothing to blame." : "";
            return;
        }
        var blame = _lines[row].Blame;
        _commitLabel.Text = blame.IsCommitted
            ? $"{blame.Commit[..7]}  {blame.Describe(_now)}"
            : $"Line {_lines[row].Line} isn't committed yet.";
    }

    private void AcceptSelection()
    {
        if (SelectedRow is { } row)
            GoToLine = _lines[row].Line;
        Application.RequestStop(this);
    }
}
