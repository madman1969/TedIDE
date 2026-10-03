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

/// <summary>One row of the Locals table: a parameter or local of the function execution stopped
/// in, with its declared type (or "?" when the source didn't say) and formatted value.</summary>
public sealed record LocalRow(string Name, string Type, string Value);

/// <summary>One call stack frame: the function (or nearest label), where it is ("main.c:24", or
/// "$080D" with no source line), and, when it has one, the source location to open - an absolute
/// path - for activating it.</summary>
public sealed record CallFrame(string Function, string Location, string? FilePath, int Line)
{
    public override string ToString() => $"{Function}  {Location}";
}

/// <summary>
/// The "Debug" tab, laid out like Visual Studio's two default debugger window groups: Locals and
/// Watch on the left, Call Stack, Breakpoints and Registers on the right, each group a small set
/// of tabs (<see cref="PaneTabs"/>) so every window gets the pane's full height. The debug state itself (running, stopped
/// in main at main.c:24) is in the window title, as in VS - AppShell sets it. Everything here is
/// refreshed by AppShell each time execution stops.
/// </summary>
public sealed class DebugPanelView : View
{
    /// <summary>VS's yellow current-statement arrow, on the innermost call stack frame.</summary>
    internal const string CurrentFrameMarker = "►";

    private readonly ListView _watchList;
    private readonly ListView _breakpointList;
    private readonly TableView _callStackTable;
    private readonly TableView _localsTable;
    private readonly TableView _registersTable;
    private readonly PaneTabs _leftGroup;
    private readonly PaneTabs _rightGroup;
    private IReadOnlyList<CallFrame> _callStack = [];
    private IReadOnlyList<BreakpointEntry> _breakpoints = [];

    /// <summary>Raised when the user activates a call stack frame that has a source location.</summary>
    public event Action<CallFrame>? FrameActivated;

    /// <summary>Raised when the user activates a breakpoint - to open its source line.</summary>
    public event Action<BreakpointEntry>? BreakpointActivated;

    public DebugPanelView()
    {
        _localsTable = Table();
        _watchList = List();
        _leftGroup = new PaneTabs(("Locals", _localsTable), ("Watch", _watchList)) { X = 0, Y = 0, Width = Dim.Percent(50), Height = Dim.Fill() };

        _callStackTable = Table();
        _callStackTable.Accepted += (_, _) =>
        {
            if (_callStackTable.Value?.SelectedCell is { } cell && cell.Y >= 0 && cell.Y < _callStack.Count && _callStack[cell.Y].FilePath is not null)
                FrameActivated?.Invoke(_callStack[cell.Y]);
        };
        _breakpointList = List();
        _breakpointList.Accepting += (_, e) =>
        {
            if (_breakpointList.SelectedItem is { } index && index >= 0 && index < _breakpoints.Count)
                BreakpointActivated?.Invoke(_breakpoints[index]);
            e.Handled = true;
        };
        _registersTable = Table();
        _rightGroup = new PaneTabs(("Call Stack", _callStackTable), ("Breakpoints", _breakpointList), ("Registers", _registersTable))
        {
            X = Pos.Right(_leftGroup) + 1, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
        };

        Add(_leftGroup, _rightGroup);
        SetLocals([]);
        SetWatches([]);
        SetCallStack([]);
        SetBreakpoints([]);
        SetRegisters(null);
    }

    private static TableView Table() => new()
    {
        FullRowSelect = true,
        ViewportSettings = ViewportSettingsFlags.HasScrollBars,
    };

    private static ListView List() => new()
    {
        ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        KeystrokeNavigator = null,
    };

    /// <summary>Replaces the call stack, innermost frame first; an empty list clears it.</summary>
    public void SetCallStack(IReadOnlyList<CallFrame> frames)
    {
        _callStack = frames;
        var table = new DataTable();
        table.Columns.Add(" ");
        table.Columns.Add("Function");
        table.Columns.Add("Location");
        foreach (var row in CallStackRows(frames))
            table.Rows.Add(row.Marker, row.Function, row.Location);
        _callStackTable.Table = new DataTableSource(table);
    }

    /// <summary>The Call Stack's rows: the current-statement marker on the innermost frame only.</summary>
    internal static IEnumerable<(string Marker, string Function, string Location)> CallStackRows(IReadOnlyList<CallFrame> frames) =>
        frames.Select((frame, i) => (i == 0 ? CurrentFrameMarker : "", frame.Function, frame.Location));

    /// <summary>Replaces the Locals table - the stopped function's parameters and locals, refreshed
    /// on every stop like the registers; an empty list clears it.</summary>
    public void SetLocals(IReadOnlyList<LocalRow> locals)
    {
        var table = new DataTable();
        table.Columns.Add("Name");
        table.Columns.Add("Value");
        table.Columns.Add("Type");
        foreach (var local in locals)
            table.Rows.Add(local.Name, local.Value, local.Type);
        _localsTable.Table = new DataTableSource(table);
    }

    /// <summary>Replaces the Breakpoints list with every breakpoint in the active project (not
    /// scoped to the currently open file) - called wherever <c>AppShell.RefreshBreakpointHighlights</c>
    /// is, since the underlying set changes at exactly the same points.</summary>
    public void SetBreakpoints(IReadOnlyList<BreakpointEntry> breakpoints)
    {
        _breakpoints = breakpoints;
        _breakpointList.SetSource(new ObservableCollection<string>(BreakpointRows(breakpoints)));
    }

    /// <summary>"● main.c:24", "○ main.c:30 (disabled)", or a hint when there are none.</summary>
    internal static IEnumerable<string> BreakpointRows(IReadOnlyList<BreakpointEntry> breakpoints) =>
        breakpoints.Count == 0
            ? ["No breakpoints. F9 sets one on the current line."]
            : breakpoints.Select(b => b.Enabled ? $"● {b.Describe()}" : $"○ {b.Describe()} (disabled)");

    /// <summary>Replaces the Watch list with each entry's label and current value (already
    /// formatted by the caller, e.g. "raster ($D012) = $34") - called after every stop (checkpoint
    /// hit or step), same as <see cref="SetRegisters"/>.</summary>
    public void SetWatches(IReadOnlyList<string> formattedWatches) =>
        _watchList.SetSource(new ObservableCollection<string>(formattedWatches.Count == 0
            ? ["No watches. Debug > Add Watch... adds one."]
            : formattedWatches));

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
