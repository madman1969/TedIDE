using Tedide.Build;
using Tedide.Core;
using Terminal.Gui.Configuration;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using GuiAttribute = Terminal.Gui.Drawing.Attribute;
using GuiCell = Terminal.Gui.Drawing.Cell;

namespace Tedide.App.Views;

/// <summary>
/// The "Output" tab: a read-only <see cref="TextView"/> of the raw build/run output, with each
/// line colored by <see cref="Cc65DiagnosticParser"/>'s reading of it (matching
/// <see cref="ErrorListView"/>'s own per-row severity coloring) - Error and Warning lines stand
/// out from plain banner/progress lines without needing to scan the text.
/// </summary>
/// <remarks>
/// Colors are baked into each line's <see cref="GuiCell"/>s when appended, not re-resolved later,
/// so switching the app's theme mid-build restyles the chrome around this pane but not lines
/// already printed - the same tradeoff <see cref="Tedide.Theming.ThemeSwitcher"/> already documents for
/// the code editor's own syntax highlighting.
/// </remarks>
public sealed class OutputView : TextView
{
    private readonly List<List<GuiCell>> _lines = [];

    public OutputView()
    {
        ReadOnly = true;
        // Auto-shown (only appears once output overflows the viewport) - same as EditorPane's editor.
        ViewportSettings = ViewportSettingsFlags.HasScrollBars;
    }

    public void Clear()
    {
        _lines.Clear();
        Load(_lines);
    }

    public void AppendLine(string line)
    {
        GuiAttribute? attribute = Cc65DiagnosticParser.TryParse(line)?.Severity switch
        {
            DiagnosticSeverity.Error => SchemeManager.GetScheme("Error").Normal,
            DiagnosticSeverity.Warning => SchemeManager.GetScheme("Warning").Normal,
            _ => null,
        };
        _lines.Add(GuiCell.ToCellList(line, attribute));
        Load(_lines);
        MoveEnd();
    }

    // TextView's own OnDrawNormalColor/OnDrawReadOnlyColor ignore each Cell's Attribute by default
    // (per their own doc: "Override to provide custom coloring... Defaults to Scheme.Normal/Focus")
    // - loading colored Cells via Load() alone has no visible effect without these overrides. Since
    // this view is always ReadOnly, OnDrawReadOnlyColor is the one actually invoked while drawing,
    // but both are overridden so this behaves correctly if ReadOnly is ever toggled off.
    protected override void OnDrawNormalColor(List<GuiCell> line, int idxCol, int idxRow)
    {
        if (idxCol < line.Count && line[idxCol].Attribute is { } attribute)
            SetAttribute(attribute);
        else
            base.OnDrawNormalColor(line, idxCol, idxRow);
    }

    protected override void OnDrawReadOnlyColor(List<GuiCell> line, int idxCol, int idxRow)
    {
        if (idxCol < line.Count && line[idxCol].Attribute is { } attribute)
            SetAttribute(attribute);
        else
            base.OnDrawReadOnlyColor(line, idxCol, idxRow);
    }
}
