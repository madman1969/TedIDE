using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Modal dialog collecting one watch expression - a known .dbg symbol name (matched with or
/// without cc65's leading underscore, same as <see cref="Tedide.Core.Debugging.DbgFile.FindEnclosingFunctionName"/>)
/// or a raw address (<c>$hex</c> or decimal) - plus whether to read it as a single byte or a
/// little-endian word. Resolving the expression (symbol lookup, number parsing) is left to the
/// caller (<see cref="AppShell"/>), which owns the active <see cref="Tedide.Core.Debugging.DbgFile"/>;
/// this dialog only requires the field isn't left blank.
/// </summary>
public sealed class AddWatchDialog : Dialog
{
    private readonly TextField _expressionField;
    private readonly CheckBox _wordSizeField;

    /// <summary>The entered expression, or null if the dialog was cancelled or left blank.</summary>
    public string? Expression { get; private set; }

    /// <summary>1 for a single byte, 2 for a little-endian word - only meaningful when <see cref="Expression"/> is non-null.</summary>
    public int Size { get; private set; } = 1;

    public AddWatchDialog()
    {
        Title = "Add Watch";
        Width = 54;
        Height = 12;
        // A real Padding adornment (rather than hand-offsetting every child's X/Y by 1) - see
        // GoToLineDialog's own comment on this same convention.
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;

        var expressionLabel = new Label { Text = "Symbol name or address ($hex/decimal):", X = 0, Y = 0 };
        _expressionField = new TextField { X = 0, Y = 2, Width = Dim.Fill(1) };

        _wordSizeField = new CheckBox { Text = "Read as a 2-byte word (little-endian)", X = 0, Y = 4 };

        var addButton = new Button { Text = "_Add", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        addButton.Accepting += (_, e) =>
        {
            var text = _expressionField.Text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                Expression = text;
                Size = _wordSizeField.Value == CheckState.Checked ? 2 : 1;
                Application.RequestStop(this);
            }
            // Only a non-blank expression should dismiss the dialog - same pattern as
            // GoToLineDialog's range check on the Accept command.
            e.Handled = true;
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        cancelButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([expressionLabel, _expressionField, _wordSizeField, addButton, cancelButton]);
        _expressionField.SetFocus();
    }
}
