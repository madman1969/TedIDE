using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Modal "Find in Files" dialog: searches every source/header/assembly file in the loaded
/// solution's project directories (via <see cref="SolutionExplorerTree.EnumerateFiles"/>, so it
/// covers the same .c/.h/.s/.asm/.inc set the Solution Explorer shows) for a plain-text,
/// case-insensitive substring and lists every matching line. Accepting a result (Enter or
/// double-click) closes the dialog with <see cref="SelectedMatch"/> set; the host is responsible
/// for actually opening that file and moving the caret to it - this dialog only searches.
/// </summary>
public sealed class FindInFilesDialog : Dialog
{
    /// <summary>One matching line. <see cref="FilePath"/> is absolute (for opening); <see cref="DisplayPath"/>
    /// is relative to the owning project's directory (for a readable results list).</summary>
    public sealed record Match(string FilePath, string DisplayPath, int LineNumber, int ColumnNumber, string LineText)
    {
        public override string ToString() => $"{DisplayPath}({LineNumber},{ColumnNumber}): {LineText.Trim()}";
    }

    private readonly Workspace _workspace;
    private readonly TextField _searchField;
    private readonly ListView _resultsList;
    private readonly Label _statusLabel;
    private List<Match> _matches = [];

    /// <summary>The match the user activated, or null if the dialog was cancelled without picking one.</summary>
    public Match? SelectedMatch { get; private set; }

    /// <param name="initialSearchText">Pre-populates the search field with this text and runs the
    /// search immediately - e.g. the editor's current selection, via the right-click context menu.
    /// Left blank (the default) for a plain "Find in Files..." with nothing to start from.</param>
    public FindInFilesDialog(Workspace workspace, string initialSearchText = "")
    {
        _workspace = workspace;

        Title = "Find in Files";
        Width = 100;
        Height = 28;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);

        var searchLabel = new Label { Text = "Find what:", X = 0, Y = 0 };
        // Y = 2, not 1: a blank row between the label and its field, same as every other field
        // in the app - see the "every field needs clearance on all 4 sides" convention.
        _searchField = new TextField { X = 0, Y = 2, Width = Dim.Fill(14) };

        // The primary action: Accent-scheme so it visually pops against the dialog's normal
        // chrome, the same accent color the app uses for the menu bar's own highlighted items.
        var findButton = new Button { Text = "_Find", IsDefault = true, SchemeName = "Accent", X = Pos.AnchorEnd(12), Y = 2, Width = 12 };
        findButton.Accepting += (_, e) =>
        {
            RunSearch();
            // Without this, the unhandled Accept command bubbles up and the Dialog's default
            // handling closes it - Find should just refresh the results, not dismiss the dialog.
            e.Handled = true;
        };

        _statusLabel = new Label { Text = string.Empty, X = 0, Y = 4, Width = Dim.Fill() };

        var resultsFrame = new FrameView { Title = "Results (Enter to open)", X = 0, Y = 6, Width = Dim.Fill(1), Height = Dim.Fill(2) };
        _resultsList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            // Auto-shown (only appears once the match list overflows the viewport) - same as
            // EditorPane's editor and the Output pane.
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        _resultsList.Accepted += (_, _) => AcceptSelection();
        resultsFrame.Add(_resultsList);

        var openButton = new Button { Text = "_Open", SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        openButton.Accepting += (_, e) =>
        {
            AcceptSelection();
            // AcceptSelection() closes the dialog itself (via RequestStop) when there's a valid
            // selection; either way, don't let an unhandled Accept bubble up and close it out
            // from under a no-selection click.
            e.Handled = true;
        };

        var closeButton = new Button { Text = "Close", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        closeButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([searchLabel, _searchField, findButton, _statusLabel, resultsFrame, openButton, closeButton]);
        _searchField.SetFocus();

        if (initialSearchText.Length > 0)
        {
            _searchField.Text = initialSearchText;
            RunSearch();
        }
    }

    /// <summary>
    /// Re-runs the search for the current search field text across every project directory in
    /// the workspace and refreshes the results list. Does nothing for blank search text.
    /// </summary>
    private void RunSearch()
    {
        var term = _searchField.Text.Trim();
        if (term.Length == 0)
            return;

        _matches = FindMatches(term).ToList();
        _resultsList.SetSource(new ObservableCollection<Match>(_matches));

        var fileCount = _matches.Select(m => m.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        _statusLabel.Text = _matches.Count == 0
            ? $"No matches for \"{term}\"."
            : $"{_matches.Count} match(es) in {fileCount} file(s).";
        // Borrow the app's Error/Accent scheme slots to make the outcome legible at a glance,
        // not just from the wording - a red "no matches" vs. a highlighted match count.
        _statusLabel.SchemeName = _matches.Count == 0 ? "Error" : "Accent";
        _statusLabel.SetNeedsDraw();
    }

    private IEnumerable<Match> FindMatches(string term)
    {
        foreach (var project in _workspace.Projects)
        {
            if (!Directory.Exists(project.Directory))
                continue;

            var files = SolutionExplorerTree.EnumerateFiles(project.Directory)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file);
                }
                catch (IOException)
                {
                    // Skip files that can't be read (e.g. locked by another process) rather than
                    // aborting the whole search.
                    continue;
                }

                var displayPath = Path.GetRelativePath(project.Directory, file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var column = lines[i].IndexOf(term, StringComparison.OrdinalIgnoreCase);
                    if (column >= 0)
                        yield return new Match(file, displayPath, i + 1, column + 1, lines[i]);
                }
            }
        }
    }

    private void AcceptSelection()
    {
        if (_resultsList.SelectedItem is not { } index || index < 0 || index >= _matches.Count)
            return;

        SelectedMatch = _matches[index];
        Application.RequestStop(this);
    }
}
