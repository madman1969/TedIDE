using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.DocViewer;

/// <summary>
/// <see cref="Markdown"/>, with a "Find..." entry on its right-click context menu.
///
/// Terminal.Gui's own docs confirm there is no documented/supported extension point for this -
/// the base view's right-click menu (Select All, Copy) is built by a private
/// <c>CreateContextMenu()</c> and shown by a private <c>ShowContextMenu()</c>, both called
/// directly from <c>OnMouseEvent</c>'s right-click handling, with no virtual hook or event to
/// customize the item list. Two earlier attempts to patch the *result* of that private mechanism
/// (appending to <c>ContextMenu.Root</c> after the base class rebuilds it) failed - the append
/// happened, but the already-shown popover didn't pick it up, since <c>ShowContextMenu</c>
/// unconditionally rebuilds <c>ContextMenu</c> from scratch on every right-click (not just on
/// focus change), discarding whatever was appended a moment earlier or even in the same call.
///
/// This instead skips the base class's right-click handling entirely and shows a self-contained
/// replacement menu (Select All, Copy, Find) built and displayed in one step every time, so
/// there's no "append after the fact" timing to get wrong. Select All/Copy still run the base
/// view's own <c>Command.SelectAll</c>/<c>Command.Copy</c> handlers (the same
/// <c>new MenuItem(this, Command.X)</c> binding the base class's own menu uses), so they behave
/// identically to before. The one dropped feature: right-clicking directly on a link no longer
/// offers "Copy Link" - the base class's own link-hit-testing behind that
/// (<c>_contextMenuLinkUrl</c>/<c>FindLinkUrlAt</c>) is private too, with no way to reuse it here.
/// </summary>
internal sealed class FindableMarkdown : Markdown
{
    public event Action? FindRequested;

    private PopoverMenu? _menu;

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.FastHasFlags(MouseFlags.RightButtonClicked))
        {
            if (!HasFocus && CanFocus)
                SetFocus();

            ShowMenu(mouse.ScreenPosition);
            return true;
        }

        return base.OnMouseEvent(mouse);
    }

    private void ShowMenu(Point screenPosition)
    {
        _menu?.Dispose();

        List<View?> items =
        [
            new MenuItem(this, Command.SelectAll),
            new MenuItem(this, Command.Copy),
            new Line(),
            new MenuItem("_Find...", "", () => FindRequested?.Invoke()),
        ];

        _menu = new PopoverMenu(items);
        App?.Popovers?.Register(_menu);
        _menu.MakeVisible(screenPosition);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            App?.Popovers?.DeRegister(_menu);
            _menu?.Dispose();
            _menu = null;
        }

        base.Dispose(disposing);
    }
}
