using System.Collections.ObjectModel;
using System.Data;
using Tedide.Core;
using Tedide.Debug;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>A user-requested memory watch - a label (the expression as typed, e.g. a symbol name
/// or a raw address) plus the resolved address and read size. Not persisted across sessions
/// (unlike <see cref="BreakpointEntry"/>): a fresh debug session's own <c>DbgFile</c> may resolve
/// the same symbol name to a different address, so watches are re-entered per session rather than
/// carrying stale addresses forward.</summary>
public sealed record WatchEntry(string Label, ushort Address, int Size);

/// <summary>
/// The "Debug" tab: a status line (not debugging / connecting / running / stopped-at-file:line,
/// now including the enclosing function name when it's resolvable), a compact breakpoints strip
/// (every breakpoint for the active project, not just the current file - unlike the editor's own
/// red-line highlighting), a compact watches strip (arbitrary addresses/symbols, refreshed from
/// VICE each time execution stops), a short "recent stops" history, and a register table refreshed
/// from a <see cref="RegisterSnapshot"/> each time execution stops (see AppShell's own debugging
/// wiring). The full breakpoint list (toggle/delete) still lives in <see cref="BreakpointsDialog"/> -
/// the strip here is read-only, just enough to see what's armed without leaving this tab.
/// </summary>
public sealed class DebugPanelView : View
{
    private readonly Label _statusLabel;
    private readonly Label _breakpointsLabel;
    private readonly Label _watchesLabel;
    private readonly ListView _historyList;
    private readonly TableView _registersTable;
    private readonly ObservableCollection<string> _history = [];

    private const int MaxHistoryEntries = 20;

    public DebugPanelView()
    {
        _statusLabel = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = "Not debugging." };
        _breakpointsLabel = new Label { X = 0, Y = 1, Width = Dim.Fill(), Text = "Breakpoints: none" };
        _watchesLabel = new Label { X = 0, Y = 2, Width = Dim.Fill(), Text = "Watches: none" };
        _historyList = new ListView
        {
            X = 0, Y = 3, Width = Dim.Fill(), Height = 6,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        _historyList.SetSource(_history);
        _registersTable = new TableView
        {
            X = 0, Y = Pos.Bottom(_historyList), Width = Dim.Fill(), Height = Dim.Fill(),
            FullRowSelect = true,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        };
        Add(_statusLabel, _breakpointsLabel, _watchesLabel, _historyList, _registersTable);
        SetRegisters(null);
    }

    public void SetStatus(string text) => _statusLabel.Text = text;

    public string StatusText => _statusLabel.Text;

    /// <summary>Replaces the breakpoints strip from every breakpoint in the active project (not
    /// scoped to the currently open file) - called wherever <c>AppShell.RefreshBreakpointHighlights</c>
    /// is, since the underlying set changes at exactly the same points.</summary>
    public void SetBreakpoints(IReadOnlyList<BreakpointEntry> breakpoints)
    {
        if (breakpoints.Count == 0)
        {
            _breakpointsLabel.Text = "Breakpoints: none";
            return;
        }

        var entries = breakpoints.Select(b => b.Enabled
            ? $"{b.SourceFile}:{b.Line}"
            : $"{b.SourceFile}:{b.Line} (disabled)");
        _breakpointsLabel.Text = $"Breakpoints: {string.Join(", ", entries)}";
    }

    /// <summary>Replaces the watches strip with each entry's label and current value (already
    /// formatted by the caller, e.g. "raster ($D012) = $34" or "score ($033C) = $1234") - called
    /// after every stop (checkpoint hit or step), same as <see cref="SetRegisters"/>.</summary>
    public void SetWatches(IReadOnlyList<string> formattedWatches)
    {
        _watchesLabel.Text = formattedWatches.Count == 0
            ? "Watches: none"
            : $"Watches: {string.Join(", ", formattedWatches)}";
    }

    /// <summary>Prepends one line to the "recent stops" history (most recent first), trimming to
    /// the last <see cref="MaxHistoryEntries"/> - called whenever a checkpoint hit or step lands
    /// somewhere new.</summary>
    public void AddHistoryEntry(string text)
    {
        _history.Insert(0, text);
        if (_history.Count > MaxHistoryEntries)
            _history.RemoveAt(_history.Count - 1);
        _historyList.SetSource(_history);
    }

    /// <summary>Clears the history - called when a debug session starts or stops, so a new
    /// session doesn't show the previous one's stops.</summary>
    public void ClearHistory()
    {
        _history.Clear();
        _historyList.SetSource(_history);
    }

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
                {
                    // "FL" is the 6502 processor status register - show it decoded (N V - B D I Z C,
                    // set bits as their letter, clear bits as ".") alongside the raw hex, since the
                    // hex alone means checking a reference table for anything but the most common
                    // flags.
                    var decoded = string.Equals(name, "FL", StringComparison.OrdinalIgnoreCase)
                        ? $" [{DecodeStatusFlags((byte)value)}]"
                        : "";
                    table.Rows.Add(name, $"${value:X4}{decoded}");
                }
            }
        }
        _registersTable.Table = new DataTableSource(table);
    }

    private static string DecodeStatusFlags(byte value)
    {
        const string names = "NV-BDIZC";
        var chars = new char[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var bit = (value >> (names.Length - 1 - i)) & 1;
            chars[i] = bit == 1 ? names[i] : '.';
        }
        return new string(chars);
    }
}
