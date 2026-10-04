using Tedide.Theming;
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
/// the view is scrolled to the nearest heading above the match, then down by an estimate of the
/// remaining distance (see <see cref="ScrollToApproximateMatch"/>), rather than to a precise
/// highlighted location: word-wrapping and tables mean a raw source line doesn't map exactly to a
/// rendered row, and the rendered rows themselves aren't public.
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

        // HotKeySpecifier disabled: the "not found" message echoes the search term, and a Label
        // reads its first "_" as a hotkey marker.
        _statusLabel = new Label { Text = string.Empty, X = 0, Y = 4, Width = Dim.Fill(1), HotKeySpecifier = TerminalGuiWorkarounds.NoHotKey };

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

    /// <summary>
    /// Scrolls the match into view. The rendered lines aren't public in Terminal.Gui 2.4.17, so
    /// the nearest heading above the match is used as an exact reference point instead - the view
    /// can scroll to a heading precisely via <see cref="Markdown.ScrollToAnchor"/> - and only the
    /// short distance from that heading is estimated, from source lines. Word-wrapping only ever
    /// adds rendered lines, so that estimate lands at or just above the match; a small margin
    /// keeps it clear of the top edge. Before, the whole page's position was estimated from the
    /// match's character offset, which on a long manual could land screens away.
    /// </summary>
    private void ScrollToApproximateMatch(string text, int matchIndex)
    {
        if (FindHeadingAbove(text, matchIndex) is { } heading && TryScrollToHeading(heading.Slug))
        {
            var sourceLinesBelow = CountNewlines(text, heading.EndIndex, matchIndex);
            var y = Math.Clamp(_contentView.Viewport.Y + Math.Max(0, sourceLinesBelow - 2), 0, Math.Max(0, _contentView.LineCount - 1));
            _contentView.Viewport = _contentView.Viewport with { Y = y };
            return;
        }

        // No usable heading (or the anchor didn't match): estimate from the character offset.
        var fraction = text.Length == 0 ? 0.0 : (double)matchIndex / text.Length;
        var targetY = (int)(fraction * _contentView.LineCount);
        targetY = Math.Clamp(targetY, 0, Math.Max(0, _contentView.LineCount - 1));
        _contentView.Viewport = _contentView.Viewport with { Y = targetY };
    }

    private bool TryScrollToHeading(string slug) => _contentView.ScrollToAnchor(slug);

    /// <summary>The last ATX heading ("## Title") that starts before <paramref name="index"/>, skipping
    /// fenced code blocks (a C "# comment" or "#define" there is not a heading), with the anchor
    /// slug the Markdown view gives it - see <see cref="Slug"/> - including the "-1", "-2" suffixes
    /// for repeated headings. Null if there's no heading above the match.</summary>
    internal static (int EndIndex, string Slug)? FindHeadingAbove(string text, int index)
    {
        var counts = new Dictionary<string, int>();
        (int, string)? last = null;
        var inFence = false;

        var lineStart = 0;
        while (lineStart < index && lineStart < text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = text.Length;
            var line = text[lineStart..lineEnd].TrimEnd('\r');

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                inFence = !inFence;
            else if (!inFence && HeadingText(line) is { } heading)
                last = (lineEnd, Unique(Slug(heading), counts));

            lineStart = lineEnd + 1;
        }
        return last;
    }

    internal static string? HeadingText(string line)
    {
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
            hashes++;
        if (hashes is 0 or > 6 || hashes >= line.Length || line[hashes] != ' ')
            return null;
        return line[(hashes + 1)..].Trim().TrimEnd('#').Trim();
    }

    /// <summary>The anchor slug Terminal.Gui's Markdown view gives a heading (its own
    /// GenerateAnchorSlug is internal): lowercase, drop everything but word characters, whitespace
    /// and hyphens, and turn each space into a hyphen. The docs builder's MarkdownSlug produces the
    /// same, and the Doc Viewer tests check both against the view's own function.</summary>
    internal static string Slug(string heading) =>
        System.Text.RegularExpressions.Regex.Replace(heading.Trim().ToLowerInvariant(), @"[^\w\s-]", "")
            .Replace(' ', '-').Trim('-');

    private static string Unique(string slug, Dictionary<string, int> counts)
    {
        var count = counts.GetValueOrDefault(slug);
        counts[slug] = count + 1;
        return count == 0 ? slug : $"{slug}-{count}";
    }

    private static int CountNewlines(string text, int from, int to)
    {
        var count = 0;
        for (var i = Math.Max(0, from); i < to && i < text.Length; i++)
            if (text[i] == '\n')
                count++;
        return count;
    }
}
