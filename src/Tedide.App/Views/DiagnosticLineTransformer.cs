using Tedide.Core;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor.Rendering;

namespace Tedide.App.Views;

/// <summary>
/// Underlines the lines of the shown file that checking as you type found a problem on, in the
/// theme's error or warning colour - see <see cref="LiveErrorChecking"/>. The text keeps its own
/// background, so a breakpoint's or the debugger's whole-line highlight (registered after this,
/// so they win on the same line) still reads as theirs.
/// </summary>
public sealed class DiagnosticLineTransformer : IVisualLineTransformer
{
    /// <summary>The worst problem on each 1-based line of the shown file.</summary>
    public Dictionary<int, DiagnosticSeverity> Lines { get; } = [];

    public void Transform(CellVisualLine line)
    {
        if (!Lines.TryGetValue(line.DocumentLine.LineNumber, out var severity))
            return;

        var marker = SchemeManager.GetScheme(severity == DiagnosticSeverity.Error ? "Error" : "Warning").Normal.Foreground;
        foreach (var element in line.Elements)
        {
            var attribute = element.Attribute;
            element.Attribute = attribute with { Foreground = marker, Style = attribute.Style | TextStyle.Underline };
        }
    }
}
