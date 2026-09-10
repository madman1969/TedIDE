using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.DocViewer;

/// <summary>
/// Modal full-text search dialog over every bundled page (via <see cref="DocDatabase.Search"/>'s
/// SQLite FTS5 index) - mirrors Tedide.App's FindInFilesDialog layout and behavior almost exactly,
/// down to the Accent/Error status coloring, since it's the same "search box, results list, Enter or
/// double-click to open" shape.
/// </summary>
public sealed class SearchDialog : Dialog
{
    private readonly DocDatabase _database;
    private readonly TextField _searchField;
    private readonly ListView _resultsList;
    private readonly Label _statusLabel;
    private List<SearchResult> _results = [];

    /// <summary>The result the user activated, or null if the dialog was cancelled without picking one.</summary>
    public SearchResult? SelectedResult { get; private set; }

    public SearchDialog(DocDatabase database)
    {
        _database = database;

        Title = "Search Documentation";
        Width = 100;
        Height = 28;
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;

        var searchLabel = new Label { Text = "Search for:", X = 0, Y = 0 };
        _searchField = new TextField { X = 0, Y = 2, Width = Dim.Fill(14) };
        // Also handled here, not just on searchButton below: pressing Enter in the field falls
        // through to the dialog's IsDefault button (searchButton) and correctly runs the search
        // either way - but only handling it on the button still leaves the field's own Accept
        // unhandled, and an unhandled Accept from the actually-focused view is what the Dialog's
        // default behavior reads as "close me" (confirmed by direct testing: without this handler,
        // the search ran - the button's own Accepting did fire - but the dialog closed anyway).
        _searchField.Accepting += (_, e) =>
        {
            RunSearch();
            e.Handled = true;
        };

        var searchButton = new Button { Text = "_Search", IsDefault = true, SchemeName = "Accent", X = Pos.AnchorEnd(12), Y = 2, Width = 12 };
        searchButton.Accepting += (_, e) =>
        {
            RunSearch();
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
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        _resultsList.Accepted += (_, _) => AcceptSelection();
        resultsFrame.Add(_resultsList);

        var openButton = new Button { Text = "_Open", SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        openButton.Accepting += (_, e) =>
        {
            AcceptSelection();
            e.Handled = true;
        };

        var closeButton = new Button { Text = "Close", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        closeButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([searchLabel, _searchField, searchButton, _statusLabel, resultsFrame, openButton, closeButton]);
        _searchField.SetFocus();
    }

    private void RunSearch()
    {
        var term = _searchField.Text.Trim();
        if (term.Length == 0)
            return;

        _results = _database.Search(term).ToList();
        _resultsList.SetSource(new ObservableCollection<string>(_results.Select(r => $"[{r.Book}] {r.FileName}: {r.Snippet}")));

        _statusLabel.Text = _results.Count == 0
            ? $"No matches for \"{term}\"."
            : $"{_results.Count} match(es).";
        _statusLabel.SchemeName = _results.Count == 0 ? "Error" : "Accent";
        _statusLabel.SetNeedsDraw();
    }

    private void AcceptSelection()
    {
        if (_resultsList.SelectedItem is not { } index || index < 0 || index >= _results.Count)
            return;

        SelectedResult = _results[index];
        Application.RequestStop(this);
    }
}
