using Terminal.Gui.Configuration;
using Terminal.Gui.Editor.Rendering;

namespace Tedide.App.Views;

/// <summary>
/// Highlights the source line execution is currently stopped at during a debug session, by
/// overwriting every element's <see cref="CellVisualLineElement.Attribute"/> on that one line to
/// the app's "Accent" scheme (the same color used for the Save button in dialogs and the menu bar's
/// own highlights - see the Dialog UI conventions). Confirmed via reflection against
/// Terminal.Gui.Editor 2.5.7 that this is how the library itself implements line-level recoloring
/// (see <c>VisualLineBuilder.ApplySelection</c>'s own doc comment: "Overwrites
/// CellVisualLineElement.Attribute with the selected attribute") - <see cref="CellVisualLineElement"/>
/// is a reference type, so mutating each element found via <see cref="CellVisualLine.Elements"/>
/// persists into what actually gets drawn, no <c>ReplaceElements</c> call needed.
///
/// Registered on <c>Editor.LineTransformers</c> (not <c>BackgroundRenderers</c> - that interface's
/// <c>Draw</c> method works at a lower level, painting raw cell rectangles, and this per-element
/// recoloring approach was the one the library's own doc comments pointed to as the supported way
/// to highlight a whole line).
/// </summary>
public sealed class CurrentDebugLineTransformer : IVisualLineTransformer
{
    /// <summary>The 1-based document line to highlight, or null while not stopped at one.</summary>
    public int? CurrentLineNumber { get; set; }

    public void Transform(CellVisualLine line)
    {
        if (CurrentLineNumber is not { } target || line.DocumentLine.LineNumber != target)
            return;

        var highlight = SchemeManager.GetScheme("Accent").Normal;
        foreach (var element in line.Elements)
            element.Attribute = highlight;
    }
}
