using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Asks for a breakpoint's condition, in VICE's own monitor syntax - VICE evaluates it, so the
/// examples shown are ones checked against a live VICE 3.9. Blank means "always stop". Whether VICE
/// accepts it is only known once a debug session sets it (see AppShell's checkpoint setup, which
/// reports a rejected condition).
/// </summary>
public sealed class BreakpointConditionDialog : Dialog
{
    private readonly TextField _conditionField;

    /// <summary>The condition entered ("" to clear it), or null if the dialog was cancelled.</summary>
    public string? Condition { get; private set; }

    public BreakpointConditionDialog(string location, string? currentCondition)
    {
        Title = "Breakpoint Condition";
        Width = 72;
        Height = 15;
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;

        // HotKeySpecifier disabled: the location is a file name, often with underscores.
        var noHotKey = new System.Text.Rune(0xFFFF);
        var prompt = new Label { Text = $"Stop at {location} only when:", X = 0, Y = 0, HotKeySpecifier = noHotKey };
        _conditionField = new TextField { X = 0, Y = 2, Width = Dim.Fill(1), Text = currentCondition ?? string.Empty };
        var help = new Label
        {
            Text = "VICE monitor syntax - registers A, X, Y, SP, PC; memory as @cpu:$addr. For example:\n"
                + "  A == $05        X != $00        @cpu:$d020 == $0e\n"
                + "Leave it blank to stop every time.",
            X = 0, Y = 4, Width = Dim.Fill(1), Height = 3,
            HotKeySpecifier = noHotKey,
        };

        var okButton = new Button { Text = "_OK", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        okButton.Accepting += (_, e) =>
        {
            Condition = _conditionField.Text.Trim();
            Application.RequestStop(this);
            e.Handled = true;
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        cancelButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([prompt, _conditionField, help, okButton, cancelButton]);
        _conditionField.SetFocus();
    }
}
