using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Tedide's own replacement for Terminal.Gui's built-in <see cref="MessageBox"/> static helper -
/// same shape (title, message, button labels in, selected button index or null for Esc/Cancel
/// out), but built as a real <see cref="Dialog"/> under our control, so it can follow the same
/// padding/layout conventions as every other Tedide dialog (real <c>Padding</c> adornment,
/// non-resizable, primary/default button in the "Accent" scheme - see the Dialog UI conventions).
/// The library's own <see cref="MessageBox"/> has no extension point for this - its <c>Dialog</c>
/// instance is entirely local to a private method (confirmed by reading <c>MessageBox.cs</c> in a
/// local Terminal.Gui source checkout) - so this reimplements it rather than styling it from outside.
///
/// <see cref="Dialog"/> (the non-generic, <c>Dialog&lt;int&gt;</c> convenience class every other
/// Tedide dialog already derives from) automatically sets its own <see cref="Dialog.Result"/> to
/// the clicked button's index when any of <see cref="Dialog{TResult}.Buttons"/> is pressed, and
/// leaves it <see langword="null"/> for Esc - no manual per-button click wiring needed here, just
/// read <c>dialog.Result</c> after <c>Application.Run</c> returns.
/// </summary>
public static class TedideMessageBox
{
    /// <summary>An informational/confirmation dialog using the normal Dialog scheme.</summary>
    public static int? Query(string title, string message, params string[] buttons) =>
        Show(title, message, useErrorScheme: false, buttons);

    /// <summary>Same as <see cref="Query"/> but styled with the Error scheme, for validation
    /// failures and other error conditions.</summary>
    public static int? ErrorQuery(string title, string message, params string[] buttons) =>
        Show(title, message, useErrorScheme: true, buttons);

    private static int? Show(string title, string message, bool useErrorScheme, string[] buttons)
    {
        using var dialog = new Dialog
        {
            Title = title,
            Text = message,
            TextAlignment = Alignment.Center,
            VerticalTextAlignment = Alignment.Center,
            SchemeName = useErrorScheme ? "Error" : "Dialog",
            // A literal "_" in an error/confirmation message (e.g. a file or symbol name) would
            // otherwise be misread as a mnemonic - see the Terminal.Gui hotkey gotchas memory.
            HotKeySpecifier = new Rune(0xFFFF),
        };
        dialog.TextFormatter.WordWrap = true;
        // Dialogs default to Movable|Resizable in Terminal.Gui - fixed size here, same as every
        // other Tedide dialog (nothing here benefits from runtime resizing).
        dialog.Arrangement &= ~ViewArrangement.Resizable;

        dialog.Buttons = [.. buttons.Select(text => new Button { Text = text })];
        // The last button is the one Dialog.AddButton (called by the Buttons setter above) already
        // marks IsDefault - same pairing every other Tedide dialog uses for its primary action.
        dialog.Buttons[^1].SchemeName = "Accent";

        // Set after Buttons, not before: AddButton reserves its own Bottom padding for the button
        // row as it's added, so only the other 3 sides need setting here - doing this first would
        // risk depending on exactly how much Bottom space AddButton decides it needs.
        dialog.Padding.Thickness = dialog.Padding.Thickness with { Left = 2, Top = 1, Right = 2 };

        Application.Run(dialog);
        return dialog.Result;
    }
}
