using System.Data;
using Tedide.Core.Debugging;
using Terminal.Gui.Configuration;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>One line of the Disassembly tab: the instruction and what to note beside it - the label
/// its operand refers to, or the source line its address belongs to.</summary>
public sealed record DisassemblyRow(DisassembledInstruction Instruction, bool IsCurrent, string Note);

/// <summary>
/// The "Disassembly" tab: the 6502 code around the PC each time execution stops, decoded from
/// memory read from VICE (see <see cref="Disassembler6502"/>) - the current instruction marked and
/// highlighted, each with its bytes, its cycle count ("*" one more on a page crossing, "**" for a
/// branch) and a note. Activating a row raises <see cref="SourceRequested"/> with its address, so
/// the host can show that address's source line.
/// </summary>
public sealed class DisassemblyView : View
{
    private readonly Label _statusLabel;
    private readonly TableView _table;
    private IReadOnlyList<DisassemblyRow> _rows = [];

    public event Action<ushort>? SourceRequested;

    public DisassemblyView()
    {
        CanFocus = true;
        _statusLabel = new Label
        {
            Text = "Start debugging to see the code around the PC.",
            X = 0, Y = 0, Width = Dim.Fill(),
            HotKeySpecifier = new System.Text.Rune(0xFFFF),
        };
        _table = new TableView
        {
            X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(),
            FullRowSelect = true,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        _table.Style.RowColorGetter = args => args.RowIndex >= 0 && args.RowIndex < _rows.Count && _rows[args.RowIndex].IsCurrent
            ? SchemeManager.GetScheme("Accent")
            : null;
        _table.Accepted += (_, _) =>
        {
            if (_table.Value?.SelectedCell is { } cell && cell.Y >= 0 && cell.Y < _rows.Count)
                SourceRequested?.Invoke(_rows[cell.Y].Instruction.Address);
        };

        Add(_statusLabel, _table);
        Show([], "Start debugging to see the code around the PC.");
    }

    public void Show(IReadOnlyList<DisassemblyRow> rows, string status)
    {
        _rows = rows;
        _statusLabel.Text = status;

        var table = new DataTable();
        table.Columns.Add(" ");
        table.Columns.Add("Address");
        table.Columns.Add("Bytes");
        table.Columns.Add("Instruction");
        table.Columns.Add("Cycles");
        table.Columns.Add("Note");
        foreach (var row in rows)
        {
            var i = row.Instruction;
            table.Rows.Add(row.IsCurrent ? ">" : "", $"${i.Address:X4}", i.BytesText, i.Text, i.Cycles, row.Note);
        }
        _table.Table = new DataTableSource(table);

        // Select the current instruction and scroll it into view, a few rows from the top.
        var current = rows.Select((r, index) => (r, index)).FirstOrDefault(x => x.r.IsCurrent).index;
        if (rows.Count > 0)
        {
            _table.SetSelection(0, current, false, null);
            _table.RowOffset = Math.Max(0, current - 3);
        }
    }
}
