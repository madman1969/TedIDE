using Terminal.Gui.Configuration;
using Terminal.Gui.Editor.Rendering;

namespace Tedide.App.Views;

/// <summary>
/// Highlights every line with an enabled breakpoint in the currently open file, using the app's
/// "Error" scheme (red - the conventional breakpoint color in most IDEs, and otherwise unused for
/// any persistent editor-line background: build diagnostics highlight their line via a transient
/// Selection instead - see <c>AppShell.OpenDiagnostic</c>). Same per-element Attribute-overwrite
/// technique as <see cref="CurrentDebugLineTransformer"/> - see its own doc comment for why
/// <c>Editor.LineTransformers</c> (not <c>BackgroundRenderers</c>) is the right extension point.
/// Registered before it on that list (see <c>AppShell</c>'s constructor) so the current-debug-line
/// highlight (Accent) wins when a breakpoint and the paused line are the same line - each
/// transformer runs in list order and later ones overwrite earlier ones' <c>Attribute</c> on the
/// same element.
/// </summary>
public sealed class BreakpointLineTransformer : IVisualLineTransformer
{
    /// <summary>1-based line numbers, for the currently open file only, that have an enabled
    /// breakpoint - recomputed by <c>AppShell.RefreshBreakpointHighlights</c> whenever the open
    /// file or the breakpoint set changes.</summary>
    public HashSet<int> BreakpointLines { get; } = [];

    public void Transform(CellVisualLine line)
    {
        if (!BreakpointLines.Contains(line.DocumentLine.LineNumber))
            return;

        var highlight = SchemeManager.GetScheme("Error").Normal;
        foreach (var element in line.Elements)
            element.Attribute = highlight;
    }
}
