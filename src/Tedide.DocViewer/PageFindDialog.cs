using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.DocViewer;

/// <summary>
/// Finds text within the page currently shown in the content pane - the in-page counterpart to
/// Ctrl+F's <see cref="SearchDialog"/> (which searches every page's text via Docs.db's FTS5
/// index), modeled on Tedide.App's own Editor Find (Terminal.Gui.Editor's FindReplaceDialog):
/// type a term, "Find Next" jumps to (here: scrolls toward) the next match, wrapping around to
/// the top once the end is reached, with a found/not-found status line.
///
/// The content pane is a read-only <see cref="Terminal.Gui.Views.Markdown"/> renderer, not an
/// editable text view, so there's no caret to move onto an exact match the way Editor's Find
/// does - matches are found against the raw Markdown source (<see cref="Markdown.Text"/>), and
/// the view is scrolled to the match's approximate position (its fractional offset within the raw
/// source, scaled against the view's own rendered <see cref="Markdown.LineCount"/>) rather than a
/// precise highlighted location, since headings/tables/word-wrapping mean a raw character offset
/// doesn't map exactly to a rendered row.
/// </summary>
public sealed class PageFindDialog : Dialog
{
    private readonly Markdown _contentView;
    private readonly TextField _findField;
    private readonly Label _statusLabel;

    private string _lastTerm = string.Empty;
    private int _searchFromIndex;

    public PageFindDialog(Markdown contentView)
    {
        _contentView = contentView;

        Title = "Find on Page";
        Width = 70;
        Height = 10;
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;

        var findLabel = new Label { Text = "Find:", X = 0, Y = 0 };
        _findField = new TextField { X = 0, Y = 2, Width = Dim.Fill(1) };
        // Same reasoning as SearchDialog's own _searchField.Accepting: without explicitly handling
        // Accept here too, an unhandled Accept from the focused field closes the dialog instead of
        // running the search, even though findNextButton's own Accepting also fires.
        _findField.Accepting += (_, e) =>
        {
            FindNext();
            e.Handled = true;
        };

        _statusLabel = new Label { Text = string.Empty, X = 0, Y = 4, Width = Dim.Fill(1) };

        var findNextButton = new Button { Text = "Find _Next", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        findNextButton.Accepting += (_, e) =>
        {
            FindNext();
            e.Handled = true;
        };

        var closeButton = new Button { Text = "Close", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        closeButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([findLabel, _findField, _statusLabel, findNextButton, closeButton]);
        _findField.SetFocus();
    }

    private void FindNext()
    {
        var term = _findField.Text.Trim();
        if (term.Length == 0)
            return;

        // A new search term always starts from the top, even if the previous term had left
        // _searchFromIndex partway through the page.
        if (!string.Equals(term, _lastTerm, StringComparison.Ordinal))
        {
            _lastTerm = term;
            _searchFromIndex = 0;
        }

        var text = _contentView.Text;
        var index = text.IndexOf(term, _searchFromIndex, StringComparison.OrdinalIgnoreCase);
        if (index < 0 && _searchFromIndex > 0)
            index = text.IndexOf(term, 0, StringComparison.OrdinalIgnoreCase); // wrap around once

        if (index < 0)
        {
            _statusLabel.Text = $"\"{term}\" not found on this page.";
            _statusLabel.SchemeName = "Error";
            _searchFromIndex = 0;
        }
        else
        {
            ScrollToApproximateMatch(text, index);
            _statusLabel.Text = "Match found.";
            _statusLabel.SchemeName = "Accent";
            _searchFromIndex = index + term.Length;
        }
        _statusLabel.SetNeedsDraw();
    }

    private void ScrollToApproximateMatch(string text, int matchIndex)
    {
        var fraction = text.Length == 0 ? 0.0 : (double)matchIndex / text.Length;
        var targetY = (int)(fraction * _contentView.LineCount);
        targetY = Math.Clamp(targetY, 0, Math.Max(0, _contentView.LineCount - 1));
        _contentView.Viewport = _contentView.Viewport with { Y = targetY };
    }
}
