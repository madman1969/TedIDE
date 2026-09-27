using System.Text;
using Terminal.Gui.Views;

namespace Tedide.Theming;

public static class MenuItemExtensions
{
    /// <summary>
    /// Shows <paramref name="item"/>'s help text (the dimmed second column) literally. It's a
    /// separate view that reads its first "_" as a hotkey marker, like any Terminal.Gui text - so a
    /// path or page name there lost an underscore: the Recent Projects menu showed
    /// "samples\C12880" for "samples\C128_80", and the Doc Viewer's bookmarks "nosuch_page" for
    /// "no_such_page" (both confirmed live). Returns the item, for use inside a list initializer.
    /// </summary>
    public static MenuItem WithLiteralHelpText(this MenuItem item)
    {
        var helpText = item.HelpText;
        item.HelpView.HotKeySpecifier = new Rune(0xFFFF);
        // Re-set so the text is parsed again under the new specifier.
        item.HelpText = string.Empty;
        item.HelpText = helpText;
        return item;
    }
}
