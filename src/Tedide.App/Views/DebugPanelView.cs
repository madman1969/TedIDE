using System.Data;
using Tedide.Debug;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// The "Debug" tab: a status line (not debugging / connecting / running / stopped-at-file:line)
/// plus a register table, refreshed from a <see cref="RegisterSnapshot"/> each time execution stops
/// (see AppShell's own debugging wiring). Breakpoints aren't shown here - see
/// <see cref="BreakpointsDialog"/> - to keep this panel to what's only meaningful while actively
/// debugging, the same way <see cref="ErrorListView"/> only shows the most recent build's diagnostics.
/// </summary>
public sealed class DebugPanelView : View
{
    private readonly Label _statusLabel;
    private readonly TableView _registersTable;

    public DebugPanelView()
    {
        _statusLabel = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = "Not debugging." };
        _registersTable = new TableView
        {
            X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(),
            FullRowSelect = true,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        Add(_statusLabel, _registersTable);
        SetRegisters(null);
    }

    public void SetStatus(string text) => _statusLabel.Text = text;

    public string StatusText => _statusLabel.Text;

    /// <summary>Replaces the displayed registers, or clears them (pass null) when not stopped/not debugging.</summary>
    public void SetRegisters(RegisterSnapshot? snapshot)
    {
        var table = new DataTable();
        table.Columns.Add("Register");
        table.Columns.Add("Value");
        if (snapshot is not null)
        {
            foreach (var name in snapshot.RegisterNames.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (snapshot[name] is { } value)
                    table.Rows.Add(name, $"${value:X4}");
            }
        }
        _registersTable.Table = new DataTableSource(table);
    }
}
