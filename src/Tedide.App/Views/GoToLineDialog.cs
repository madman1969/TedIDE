using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Modal dialog collecting a 1-based line number to jump the editor's caret to. Mirrors
/// <see cref="RenameFileDialog"/>'s single-field layout; validation (range against the open
/// document's line count) happens here, but moving the caret itself is left to the caller
/// (<see cref="AppShell"/>) via <see cref="LineNumber"/>, the same split <see cref="FindInFilesDialog"/>
/// uses with <c>SelectedMatch</c>/<c>AppShell.OpenMatch</c>.
/// </summary>
public sealed class GoToLineDialog : Dialog
{
    private readonly TextField _lineField;
    private readonly int _lineCount;

    /// <summary>The entered 1-based line number, or null if the dialog was cancelled or the
    /// entered value wasn't a valid line number.</summary>
    public int? LineNumber { get; private set; }

    /// <param name="currentLineNumber">1-based line the caret is currently on - pre-fills the field.</param>
    /// <param name="lineCount">The open document's total line count, for range validation.</param>
    public GoToLineDialog(int currentLineNumber, int lineCount)
    {
        _lineCount = lineCount;

        Title = "Go To Line";
        Width = 44;
        Height = 11;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) so the
        // whole dialog gets consistent breathing room from its border - children below are
        // positioned relative to this inset content area, i.e. X = 0 is already 2 cells in.
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        // Dialogs default to Movable|Resizable in Terminal.Gui (see ViewArrangement) - fixed size
        // here since there's nothing in this static, fixed-position layout that benefits from
        // resizing.
        Arrangement &= ~ViewArrangement.Resizable;

        var lineLabel = new Label { Text = $"Line number (1-{lineCount}):", X = 0, Y = 0 };
        // Y = 2, not 1: a blank row between the label and its field, same as every other field
        // in the app - see the "every field needs clearance on all 4 sides" convention.
        _lineField = new TextField { X = 0, Y = 2, Width = Dim.Fill(1), Text = currentLineNumber.ToString() };

        // The primary action: Accent-scheme so it visually pops against the dialog's normal
        // chrome, the same accent color the app uses for the menu bar's own highlighted items.
        var goButton = new Button { Text = "_Go", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        goButton.Accepting += (_, e) =>
        {
            if (int.TryParse(_lineField.Text, out var line) && line >= 1 && line <= _lineCount)
            {
                LineNumber = line;
                Application.RequestStop(this);
            }
            // Without this, the unhandled Accept command bubbles up and the Dialog's default
            // handling closes it even on invalid input - only a valid, in-range line should
            // dismiss it, same as NewFileDialog's blank-name check.
            e.Handled = true;
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        cancelButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([lineLabel, _lineField, goButton, cancelButton]);
        _lineField.SetFocus();
    }
}
