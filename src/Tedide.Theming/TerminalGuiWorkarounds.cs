using System.Text;
using Terminal.Gui.Views;

namespace Tedide.Theming;

/// <summary>
/// Every workaround for Terminal.Gui 2.4.17 behaviour, in one place, so a package update knows
/// what to re-check. Most are here; a few have to live with the code they fix, and are listed so
/// they aren't missed:
/// <list type="bullet">
/// <item><c>TerminalColors</c> (this project): Terminal.Gui forces 16 colours inside Windows
/// Terminal; true colour is switched back on at startup.</item>
/// <item><c>PaneTabs</c> (Tedide.App): a <c>Tabs</c> nested inside another <c>Tabs</c> never gets
/// mouse clicks, so the Debug tab's window groups draw their own strip.</item>
/// <item><c>OutputView.OnDrawReadOnlyColor</c> (Tedide.App): a read-only <c>TextView</c> draws its
/// text in the theme's invisible colour, and <c>TextView.Load</c> paints uncoloured cells with
/// Focus and keeps (and edits) the list it's given.</item>
/// <item><c>AppShell.OnKeyDown</c> and the status bar's shortcuts (Tedide.App): a menu item's key
/// only works while its menu is open (<c>BindKeyToApplication</c> doesn't fire for the Debug
/// menu), so app-wide keys are bound there instead.</item>
/// <item><c>SolutionExplorerTree</c> (Tedide.App): a popover menu over a tree toggles the selected
/// node when an item is chosen.</item>
/// <item><c>UiSynchronizationContext</c> (Tedide.App): Terminal.Gui installs no
/// SynchronizationContext, so awaits wouldn't come back to the UI thread.</item>
/// <item>The Doc Viewer's content menu: <c>Markdown</c> rebuilds its context menu on every
/// right-click, discarding anything added to it, so it's replaced whole.</item>
/// </list>
/// </summary>
public static class TerminalGuiWorkarounds
{
    /// <summary>
    /// A <c>HotKeySpecifier</c> no text contains. Terminal.Gui reads the first "_" in a label's,
    /// button's or title's text as a hotkey marker and drops it - "animation_step" showed as
    /// "animationstep", "CC65_HOME:" as "CC65HOME:". In a window title it also crashed: the next,
    /// shorter title came in with the old hotkey's position.
    /// </summary>
    public static readonly Rune NoHotKey = new(0xFFFF);

    /// <summary>
    /// A <c>ListView</c> with no type-to-search. Its search runs before the KeyDown event and
    /// swallows any letter that matches a row, so a list's own letter keys (D, B and H in the Git
    /// tab) never arrived. Set <c>KeystrokeNavigator = TerminalGuiWorkarounds.NoTypeToSearch</c>.
    /// </summary>
    public static readonly IListCollectionNavigator? NoTypeToSearch = null;

    /// <summary>
    /// Shows <paramref name="item"/>'s help text (the dimmed second column) literally. It's a
    /// separate view that reads its first "_" as a hotkey marker too - so a path or page name there
    /// lost an underscore: the Recent Projects menu showed "samples\C12880" for "samples\C128_80",
    /// and the Doc Viewer's bookmarks "nosuch_page" for "no_such_page". Returns the item, for use
    /// inside a list initializer.
    /// </summary>
    public static MenuItem WithLiteralHelpText(this MenuItem item)
    {
        var helpText = item.HelpText;
        item.HelpView.HotKeySpecifier = NoHotKey;
        // Re-set so the text is parsed again under the new specifier.
        item.HelpText = string.Empty;
        item.HelpText = helpText;
        return item;
    }

    /// <summary>Shows <paramref name="text"/> as a menu item's key: Terminal.Gui spells arrow keys
    /// "CursorLeft", where Visual Studio's menus say "Left". Returns the item.</summary>
    public static MenuItem WithKeyText(this MenuItem item, string text)
    {
        item.KeyView.Text = text;
        return item;
    }
}
