using Terminal.Gui.Drawing;

namespace Tedide.Theming;

/// <summary>Colour schemes for tree nodes that should stand out from their siblings - shared by
/// Tedide.App's Solution Explorer folders and Tedide.DocViewer's book titles, so both look alike.</summary>
public static class TreeNodeSchemes
{
    /// <summary>
    /// <paramref name="treeScheme"/> with unselected text in the theme's type color (CodeType - the
    /// same readability-checked color type names get in the editor: teal in VS2026 Dark, yellow in
    /// Solarized, light green in Commodore 64). Selected rows keep the tree's own Focus/Active look.
    /// </summary>
    public static Scheme Emphasised(Scheme treeScheme) => new(treeScheme)
    {
        Normal = new Terminal.Gui.Drawing.Attribute(treeScheme.CodeType.Foreground, treeScheme.Normal.Background, treeScheme.Normal.Style),
    };
}
