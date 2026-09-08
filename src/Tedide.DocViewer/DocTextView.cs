using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace Tedide.DocViewer;

/// <summary>
/// A read-only <see cref="TextView"/> that actually shows its text. Terminal.Gui 2.4.17's default
/// theme gives the "ReadOnly" visual role the same foreground as background - the same invisible-text
/// default <see cref="Tedide.App.Views.OutputView"/> works around for its own colored lines - so a
/// plain <c>TextView { ReadOnly = true }</c> renders as a blank pane. Overriding
/// <see cref="OnDrawReadOnlyColor"/> to draw with the (correctly contrasting) Normal role instead
/// fixes it without needing a custom color scheme.
/// </summary>
public sealed class DocTextView : TextView
{
    public DocTextView()
    {
        ReadOnly = true;
    }

    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow) =>
        SetAttribute(GetScheme()!.Normal);
}
