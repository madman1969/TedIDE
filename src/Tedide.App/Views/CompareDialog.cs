using System.Data;
using Tedide.Git;
using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Compare with Last Commit (git phase 3): one file's changes since HEAD as a read-only unified
/// diff - each hunk's "@@" header, then its lines with their numbers on the committed (Old) and
/// current (New) side, added lines green and removed ones red, in VS Code's hues made readable on
/// the theme's background. Enter (or Go to Line) closes it with <see cref="GoToLine"/> set to the
/// selected row's line in the current file - for a removed line, the line that now follows it; the
/// host does the navigating.
/// </summary>
public sealed class CompareDialog : Dialog
{
    private sealed record Row(DiffHunk Hunk, DiffLine? Line);

    private static readonly Color AddedHue = new(0x73, 0xC9, 0x91);
    private static readonly Color RemovedHue = new(0xE4, 0x67, 0x6B);
    private static readonly Color HeaderHue = new(0x4F, 0xC1, 0xFF);

    private readonly List<Row> _rows = [];
    private readonly TableView _table;

    /// <summary>The 1-based line of the current file to go to, or null if closed without one.</summary>
    public int? GoToLine { get; private set; }

    /// <param name="displayPath">The file's name as the title shows it, e.g. relative to its project.</param>
    /// <param name="unsaved">Whether the editor has edits not saved yet - they're part of the diff.</param>
    public CompareDialog(string displayPath, GitDiff diff, bool unsaved)
        : this($"Compare with Last Commit - {displayPath}",
            $"HEAD against the {(unsaved ? "editor's text, unsaved edits included" : "file")}:", diff, canGoToLine: true)
    {
    }

    /// <summary>
    /// Any diff - History shows a past commit's changes this way, with <paramref name="canGoToLine"/>
    /// false: its line numbers are the file's as of that commit, not the editor's.
    /// </summary>
    /// <param name="summary">What's being compared, before the change counts.</param>
    public CompareDialog(string title, string summary, GitDiff diff, bool canGoToLine)
    {
        Title = title;
        // The title echoes a file name, and a title reads its first "_" as a hotkey marker.
        HotKeySpecifier = TerminalGuiWorkarounds.NoHotKey;
        Width = Dim.Percent(90);
        Height = Dim.Percent(85);
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;

        var hunks = diff.Hunks.Count == 1 ? "1 change" : $"{diff.Hunks.Count} changes";
        var summaryLabel = new Label
        {
            Text = $"{summary}  {hunks},  +{diff.Added} -{diff.Removed}",
            X = 0, Y = 0, Width = Dim.Fill(1),
            HotKeySpecifier = TerminalGuiWorkarounds.NoHotKey,
        };

        foreach (var hunk in diff.Hunks)
        {
            _rows.Add(new Row(hunk, null));
            _rows.AddRange(hunk.Lines.Select(line => new Row(hunk, line)));
        }

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
        {
            if (args.RowIndex < 0 || args.RowIndex >= _rows.Count)
                return null;
            var hue = _rows[args.RowIndex].Line?.Kind switch
            {
                null => HeaderHue,
                DiffLineKind.Added => AddedHue,
                DiffLineKind.Removed => RemovedHue,
                _ => (Color?)null,
            };
            return hue is { } color ? Tinted(_table.GetScheme(), color) : null;
        };
        _table.Table = new DataTableSource(BuildTable());
        if (_rows.Count > 0)
            _table.SetSelection(0, FirstChange(), false, null);
        _table.Accepting += (_, e) =>
        {
            if (canGoToLine)
                AcceptSelection();
            else
                Application.RequestStop(this);
            e.Handled = true;
        };

        var closeButton = new Button { Text = "Close", X = canGoToLine ? Pos.Center() + 1 : Pos.Center() - 9, Y = Pos.AnchorEnd(1), Width = 18 };
        closeButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };
        Add([summaryLabel, _table, closeButton]);

        if (canGoToLine)
        {
            var goButton = new Button { Text = "_Go to Line", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 19, Y = Pos.AnchorEnd(1), Width = 18 };
            goButton.Accepting += (_, e) =>
            {
                AcceptSelection();
                e.Handled = true;
            };
            Add(goButton);
        }
        else
        {
            closeButton.IsDefault = true;
            closeButton.SchemeName = "Accent";
        }
        _table.SetFocus();
    }

    private DataTable BuildTable()
    {
        var table = new DataTable();
        table.Columns.Add("Old");
        table.Columns.Add("New");
        table.Columns.Add(" ");
        table.Columns.Add("Code");
        foreach (var row in _rows)
        {
            if (row.Line is not { } line)
            {
                table.Rows.Add("", "", "", row.Hunk.Header);
                continue;
            }
            var sign = line.Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "-", _ => "" };
            table.Rows.Add($"{line.OldLine,5}", $"{line.NewLine,5}", sign, line.Text.Replace("\t", "    "));
        }
        return table;
    }

    /// <summary>The first added or removed row, so the dialog opens on a change rather than context.</summary>
    private int FirstChange()
    {
        var index = _rows.FindIndex(r => r.Line is { Kind: not DiffLineKind.Context });
        return index < 0 ? 0 : index;
    }

    private void AcceptSelection()
    {
        if (_table.Value?.SelectedCell is { } cell && cell.Y >= 0 && cell.Y < _rows.Count)
            GoToLine = LineInCurrentFile(_rows[cell.Y]);
        Application.RequestStop(this);
    }

    /// <summary>A row's line in the current file: its own, or for a removed line or a hunk header,
    /// the next line the current file still has.</summary>
    private static int LineInCurrentFile(Row row)
    {
        if (row.Line?.NewLine is { } own)
            return own;
        var lines = row.Hunk.Lines;
        var start = row.Line is null ? 0 : lines.ToList().IndexOf(row.Line) + 1;
        for (var i = start; i < lines.Count; i++)
        {
            if (lines[i].NewLine is { } next)
                return next;
        }
        // "+19,0" means "after line 19": nothing's left of the hunk in the current file.
        var hunk = row.Hunk;
        return Math.Max(1, hunk.NewCount == 0 ? hunk.NewStart + 1 : hunk.NewStart + hunk.NewCount);
    }

    /// <summary><paramref name="scheme"/> with unselected text in <paramref name="hue"/>, made
    /// readable on the scheme's background - the same treatment as the Solution Explorer's git letters.</summary>
    internal static Scheme Tinted(Scheme scheme, Color hue)
    {
        var background = scheme.Normal.Background;
        return new Scheme(scheme)
        {
            Normal = new Terminal.Gui.Drawing.Attribute(ThemeSwitcher.Readable(hue, background), background, scheme.Normal.Style),
        };
    }
}
