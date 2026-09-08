using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Tedide.DocViewer;

/// <summary>
/// A read-only <see cref="TextView"/> that actually shows its text (Terminal.Gui 2.4.17's default
/// theme gives the "ReadOnly" visual role the same foreground as background - the same invisible-text
/// default <see cref="Tedide.App.Views.OutputView"/> works around for its own colored lines - so a
/// plain <c>TextView { ReadOnly = true }</c> renders as a blank pane; overriding
/// <see cref="OnDrawReadOnlyColor"/> to draw with the Normal role instead fixes it) that additionally
/// knows how to highlight and follow the internal hyperlinks <see cref="DocTextConverter"/> found in
/// the page it's showing: a link's own span is drawn in the theme's HotNormal color, Tab/Shift+Tab
/// move the caret to the next/previous link, Enter follows whichever link the caret is on or inside,
/// and a left click follows whichever link is under the pointer (which also focuses this view, like
/// any click target). Getting keyboard focus here in the first place needs an explicit
/// <see cref="Terminal.Gui.ViewBase.View.SetFocus"/> call - Tab alone can't do it, since it only
/// advances among peer views under the same immediate SuperView, and this view doesn't share one
/// with the sidebar tree; see <see cref="DocViewerShell"/>'s tree Accepted handler.
///
/// This view only knows about text positions, not the doc catalog - <see cref="DocViewerShell"/>
/// owns what following a link actually does (cross-page navigation, same-page anchor jump) via
/// <see cref="LinkActivated"/>.
/// </summary>
public sealed class DocTextView : TextView
{
    private IReadOnlyList<DocLink> _links = [];
    private Dictionary<int, List<DocLink>> _linksByRow = [];

    /// <summary>Raised when the user follows a link, by any of the input methods described above.</summary>
    public event Action<DocLink>? LinkActivated;

    /// <summary>The current page's internal hyperlinks, in document order - <see cref="DocViewerShell"/>
    /// replaces this every time it shows a different page.</summary>
    public IReadOnlyList<DocLink> Links
    {
        get => _links;
        set
        {
            _links = value;
            _linksByRow = value.GroupBy(l => l.Row).ToDictionary(g => g.Key, g => g.ToList());
            SetNeedsDraw();
        }
    }

    public DocTextView()
    {
        ReadOnly = true;

        KeyDown += (_, key) =>
        {
            if (key == Key.Enter)
            {
                if (FindLinkAt(InsertionPoint.Y, InsertionPoint.X) is { } link)
                {
                    LinkActivated?.Invoke(link);
                    key.Handled = true;
                }
            }
            else if (key == Key.Tab && NextLink(forward: true) is { } nextLink)
            {
                MoveCaretTo(nextLink);
                key.Handled = true;
            }
            else if (key == Key.Tab.WithShift && NextLink(forward: false) is { } previousLink)
            {
                MoveCaretTo(previousLink);
                key.Handled = true;
            }
        };
    }

    /// <summary>Lets the base class place the caret at the clicked position as it normally would
    /// (this view never needs to compute screen-to-document coordinates itself), then follows
    /// whatever link ended up under it, if any.</summary>
    protected override bool OnMouseEvent(Mouse mouse)
    {
        var handled = base.OnMouseEvent(mouse);
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked) &&
            FindLinkAt(InsertionPoint.Y, InsertionPoint.X) is { } link)
        {
            LinkActivated?.Invoke(link);
            return true;
        }
        return handled;
    }

    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
        var isLink = _linksByRow.TryGetValue(idxRow, out var rowLinks) &&
            rowLinks.Any(l => idxCol >= l.Column && idxCol < l.Column + l.Length);
        SetAttribute(isLink ? GetScheme()!.HotNormal : GetScheme()!.Normal);
    }

    private DocLink? FindLinkAt(int row, int column) =>
        _linksByRow.TryGetValue(row, out var rowLinks)
            ? rowLinks.FirstOrDefault(l => column >= l.Column && column < l.Column + l.Length)
            : null;

    /// <summary>The next/previous link after the caret's current position, in document order,
    /// wrapping around at either end so repeated Tab/Shift+Tab cycles through every link on the page.</summary>
    private DocLink? NextLink(bool forward)
    {
        if (_links.Count == 0)
            return null;

        var row = InsertionPoint.Y;
        var column = InsertionPoint.X;
        return forward
            ? _links.FirstOrDefault(l => l.Row > row || (l.Row == row && l.Column > column)) ?? _links[0]
            : _links.LastOrDefault(l => l.Row < row || (l.Row == row && l.Column < column)) ?? _links[^1];
    }

    private void MoveCaretTo(DocLink link) => InsertionPoint = new Point(link.Column, link.Row);
}
