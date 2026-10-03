using System.Collections.ObjectModel;
using Tedide.Core.Navigation;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// Shown by Go To Definition when a symbol has more than one candidate - a function defined
/// differently for two targets, a member name shared by two structs, a typedef repeated across
/// cc65's headers. Accepting one (Enter or double-click) closes the dialog with
/// <see cref="SelectedDefinition"/> set; the host does the navigating.
/// </summary>
public sealed class DefinitionPickerDialog : Dialog
{
    private sealed record Entry(SymbolDefinition Definition, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly List<Entry> _entries;
    private readonly ListView _list;

    public SymbolDefinition? SelectedDefinition { get; private set; }

    /// <param name="lineText">The source line a definition sits on, for the list (may be empty).</param>
    /// <param name="displayPath">Shortens an absolute path for the list, e.g. relative to its project.</param>
    public DefinitionPickerDialog(string symbol, IReadOnlyList<SymbolDefinition> definitions, Func<SymbolDefinition, string> lineText, Func<string, string> displayPath)
    {
        Title = $"Definitions of {symbol}";
        // The title echoes a symbol name, and a title reads its first "_" as a hotkey marker.
        HotKeySpecifier = new System.Text.Rune(0xFFFF);
        Width = 100;
        Height = Math.Min(28, definitions.Count + 9);
        Padding.Thickness = new Thickness(2, 1, 2, 1);
        Arrangement &= ~ViewArrangement.Resizable;

        _entries = definitions
            .Select(d => new Entry(d, $"{displayPath(d.FilePath)}({d.Line}): {d.KindText,-18} {lineText(d).Trim()}"))
            .ToList();

        // Draws its own border rather than sitting in a FrameView, so Tab can still reach it - see
        // FindInFilesDialog's results list.
        _list = new ListView
        {
            Title = "Choose one (Enter to go)",
            BorderStyle = LineStyle.Single,
            X = 0,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(2),
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        _list.SetSource(new ObservableCollection<Entry>(_entries));
        _list.SelectedItem = 0;
        _list.Accepting += (_, e) =>
        {
            AcceptSelection();
            e.Handled = true;
        };

        var goButton = new Button { Text = "_Go", IsDefault = true, SchemeName = "Accent", X = Pos.Center() - 13, Y = Pos.AnchorEnd(1), Width = 12 };
        goButton.Accepting += (_, e) =>
        {
            AcceptSelection();
            e.Handled = true;
        };

        var cancelButton = new Button { Text = "Cancel", X = Pos.Center() + 1, Y = Pos.AnchorEnd(1), Width = 12 };
        cancelButton.Accepting += (_, e) =>
        {
            Application.RequestStop(this);
            e.Handled = true;
        };

        Add([_list, goButton, cancelButton]);
        _list.SetFocus();
    }

    private void AcceptSelection()
    {
        var index = _list.SelectedItem is { } selected && selected >= 0 && selected < _entries.Count ? selected : 0;
        SelectedDefinition = _entries[index].Definition;
        Application.RequestStop(this);
    }
}
