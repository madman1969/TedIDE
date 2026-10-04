using System.Data;
using Tedide.Core.Debugging;
using Tedide.Theming;
using Terminal.Gui.Configuration;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// The "Memory" tab: a hex dump of <see cref="ByteCount"/> bytes from an address typed as a
/// symbol or $hex/decimal (the same forms Add Watch takes), read from VICE whenever execution
/// stops. Bytes that changed since the previous stop are drawn in the Accent colour. The view
/// only displays: <see cref="AddressRequested"/> and <see cref="PageRequested"/> ask the host to
/// resolve the address and read the memory, which it hands back through <see cref="Show"/>.
/// </summary>
public sealed class MemoryView : View
{
    /// <summary>How much the tab reads and shows at once: sixteen rows of sixteen bytes.</summary>
    public const int ByteCount = 256;

    private readonly TextField _addressField;
    private readonly Label _statusLabel;
    private readonly TableView _table;
    private HashSet<int> _changed = [];

    /// <summary>Raised when the user enters an address expression (Enter in the field, or Go).</summary>
    public event Action<string>? AddressRequested;

    /// <summary>Raised by the page buttons with the number of bytes to move by (+/- <see cref="ByteCount"/>).</summary>
    public event Action<int>? PageRequested;

    public MemoryView()
    {
        CanFocus = true;

        var addressLabel = new Label { Text = "Address:", X = 0, Y = 0 };
        _addressField = new TextField { X = Pos.Right(addressLabel) + 1, Y = 0, Width = 24 };
        _addressField.Accepting += (_, e) =>
        {
            AddressRequested?.Invoke(_addressField.Text.Trim());
            e.Handled = true;
        };

        var goButton = new Button { Text = "Go", X = Pos.Right(_addressField) + 1, Y = 0 };
        goButton.Accepting += (_, e) =>
        {
            AddressRequested?.Invoke(_addressField.Text.Trim());
            e.Handled = true;
        };
        var previousButton = new Button { Text = $"-${ByteCount:X}", X = Pos.Right(goButton) + 1, Y = 0 };
        previousButton.Accepting += (_, e) =>
        {
            PageRequested?.Invoke(-ByteCount);
            e.Handled = true;
        };
        var nextButton = new Button { Text = $"+${ByteCount:X}", X = Pos.Right(previousButton) + 1, Y = 0 };
        nextButton.Accepting += (_, e) =>
        {
            PageRequested?.Invoke(ByteCount);
            e.Handled = true;
        };

        // The status can echo a symbol name - no "_" hotkey parsing.
        _statusLabel = new Label
        {
            Text = "Start debugging, then enter a symbol or $address.",
            X = Pos.Right(nextButton) + 2, Y = 0, Width = Dim.Fill(),
            HotKeySpecifier = TerminalGuiWorkarounds.NoHotKey,
        };

        _table = new TableView
        {
            X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(),
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        // Columns 1-16 are the bytes: colour the ones that changed since the last stop.
        for (var column = 1; column <= MemoryDump.BytesPerRow; column++)
        {
            var byteIndex = column - 1;
            _table.Style.GetOrCreateColumnStyle(column).ColorGetter = args =>
                _changed.Contains(args.RowIndex * MemoryDump.BytesPerRow + byteIndex) ? SchemeManager.GetScheme("Accent") : null;
        }

        Add(addressLabel, _addressField, goButton, previousButton, nextButton, _statusLabel, _table);
        Show(0, [], [], null);
    }

    /// <summary>Shows <paramref name="bytes"/> read from <paramref name="address"/>, highlighting the
    /// offsets in <paramref name="changed"/>, with <paramref name="status"/> beside the controls.</summary>
    public void Show(ushort address, byte[] bytes, HashSet<int> changed, string? status)
    {
        _changed = changed;
        if (bytes.Length > 0)
            _addressField.Text = $"${address:X4}";
        if (status is not null)
            _statusLabel.Text = status;

        var table = new DataTable();
        table.Columns.Add("Address");
        for (var i = 0; i < MemoryDump.BytesPerRow; i++)
            table.Columns.Add(i.ToString("X1"));
        table.Columns.Add("Text");
        foreach (var row in MemoryDump.Rows(address, bytes))
            table.Rows.Add([$"${row.Address:X4}", .. row.Hex, row.Text]);
        _table.Table = new DataTableSource(table);
    }

    public void SetStatus(string status) => _statusLabel.Text = status;
}
