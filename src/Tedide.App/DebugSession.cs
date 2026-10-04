using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Serilog;
using Tedide.App.Views;
using Tedide.Build;
using Tedide.Core;
using Tedide.Core.Debugging;
using Tedide.Debug;
using Tedide.Theming;
using Terminal.Gui.App;

namespace Tedide.App;

/// <summary>What <see cref="DebugSession"/> needs from the shell around it.</summary>
internal interface IDebugSessionHost
{
    ViceEmulator Vice { get; }
    IDialogs Dialogs { get; }
    void AppendOutputLine(string line);
    void OnUiThread(Action action);
    /// <summary>Starts a task without waiting for it, reporting a failure.</summary>
    void Fire(Task task, string what);
    bool Guard(string action, Action body);
    void OpenSymbol((string FilePath, int LineNumber) entry);
    void CenterEditorOnLine(string filePath, int lineNumber);
    /// <summary>Where execution is stopped, for the editor's current-line highlight; null when running.</summary>
    void SetDebugLine((string FilePath, int Line)? location);
    /// <summary>The debug state in the window title; null when not debugging.</summary>
    void SetDebugStatus(string? status);
    void ShowDebugTab();
    Task<BuildResult?> BuildActiveProjectAsync();
    bool CheckStartupProjectRuns(string action);
}

/// <summary>
/// Source-level debugging against VICE: the startup project's breakpoints, a session's
/// connection, stepping, watches, locals, the call stack, and the Memory and Disassembly tabs.
/// The shell owns the views and menus and calls in here; this calls back through
/// <see cref="IDebugSessionHost"/>. Stepping and arming breakpoints are in
/// <see cref="SourceStepper"/> and <see cref="CheckpointSet"/>, which are tested without VICE.
/// </summary>
internal sealed class DebugSession
{
    private readonly IDebugSessionHost _host;
    private readonly Workspace _workspace;
    private readonly EditorPane _editorPane;
    private readonly DebugPanelView _debugPanel;
    private readonly DisassemblyView _disassemblyView;
    private readonly MemoryView _memoryView;
    private readonly BreakpointLineTransformer _breakpointLineTransformer;

    private BreakpointsFile _breakpoints = new();
    private ViceMonitorClient? _debugClient;
    private DbgFile? _dbgFile;
    private bool _isDebugging;
    private bool _isStopped;
    private bool _isStepping;
    /// <summary>The breakpoints armed in VICE this session.</summary>
    private readonly CheckpointSet _checkpoints = new();
    /// <summary>User-added memory watches for the session - see <see cref="WatchEntry"/>'s own doc
    /// comment for why these aren't saved the way <see cref="_breakpoints"/> are.</summary>
    private readonly List<WatchEntry> _watches = new();
    /// <summary>Where the Memory tab reads from, once the user has entered an address, and what it
    /// read there at the last stop - so the next stop can highlight what changed.</summary>
    private ushort? _memoryAddress;
    private byte[]? _memorySnapshot;

    public DebugSession(IDebugSessionHost host, Workspace workspace, EditorPane editorPane, DebugPanelView debugPanel,
        DisassemblyView disassemblyView, MemoryView memoryView, BreakpointLineTransformer breakpointLineTransformer)
    {
        _host = host;
        _workspace = workspace;
        _editorPane = editorPane;
        _debugPanel = debugPanel;
        _disassemblyView = disassemblyView;
        _memoryView = memoryView;
        _breakpointLineTransformer = breakpointLineTransformer;
    }

    /// <summary>The startup project's breakpoints. Callers that change them call
    /// <see cref="BreakpointsChanged"/>.</summary>
    public BreakpointsFile Breakpoints => _breakpoints;

    /// <summary>Where the Memory tab is reading, for its paging buttons.</summary>
    public ushort? MemoryAddress => _memoryAddress;

    /// <summary>After the breakpoints were changed from outside (a file renamed or a project
    /// removed): the highlights, the Debug tab, and VICE's checkpoints if a session is running.</summary>
    public void BreakpointsChanged()
    {
        RefreshBreakpointHighlights();
        Fire(SyncCheckpointsWithViceAsync());
    }

    private void Fire(Task task, [CallerArgumentExpression(nameof(task))] string what = "") => _host.Fire(task, what);

    /// <summary>Reloads <see cref="_breakpoints"/> from the active project's breakpoints sidecar
    /// file (see <see cref="TedideProject.ResolvedBreakpointsFile"/>), or resets to an empty set if
    /// no project is loaded. Called everywhere the active project itself changes (open/new/close,
    /// Project Settings save) - not on every build, since breakpoints don't change from a build.</summary>
    public void LoadBreakpointsForActiveProject()
    {
        string? problem = null;
        _breakpoints = _workspace.ActiveProject is { } project
            ? BreakpointsFile.LoadOrRecover(project.ResolvedBreakpointsFile, out problem)
            : new BreakpointsFile();
        if (problem is not null)
            _host.AppendOutputLine(problem);
        RefreshBreakpointHighlights();
    }

    /// <summary>
    /// Recomputes <see cref="_breakpointLineTransformer"/>'s highlighted line set from
    /// <see cref="_breakpoints"/>, scoped to the file being shown (the editor shows one tab at a
    /// time, and this runs again on every tab switch - see AppShell.OnActiveDocumentChanged),
    /// and refreshes the Debug tab's breakpoints strip (unscoped - every breakpoint in
    /// the project, not just the open file). Called whenever either the open file or the
    /// breakpoint set itself changes.
    /// </summary>
    public void RefreshBreakpointHighlights()
    {
        _breakpointLineTransformer.BreakpointLines.Clear();
        if (_workspace.ActiveProject is { } project && _editorPane.OpenPath is { } openPath)
        {
            var relativePath = Path.GetRelativePath(project.Directory, openPath).Replace('\\', '/');
            foreach (var breakpoint in _breakpoints.Breakpoints)
                if (breakpoint.Enabled && string.Equals(breakpoint.SourceFile, relativePath, StringComparison.OrdinalIgnoreCase))
                    _breakpointLineTransformer.BreakpointLines.Add(breakpoint.Line);
        }
        _editorPane.Editor.SetNeedsDraw();
        _debugPanel.SetBreakpoints(_breakpoints.Breakpoints);
    }

    /// <summary>
    /// Toggles a breakpoint on the currently open file's cursor line (F9) - the fallback for
    /// setting breakpoints since Terminal.Gui.Editor's Editor has no clickable gutter (see
    /// <see cref="CurrentDebugLineTransformer"/>'s own doc comment). Saves immediately so it
    /// survives even if the debug session (or Tedide itself) is closed without an explicit save.
    /// </summary>
    public void ToggleBreakpointAtCursor() => _host.Guard("Saving breakpoints", () => ToggleBreakpointAtCursorCore());

    private void ToggleBreakpointAtCursorCore()
    {
        var project = _workspace.ActiveProject;
        if (project is null || _editorPane.OpenPath is not { } openPath || _editorPane.Editor.Document is not { } document)
            return;

        var line = document.GetLineByOffset(_editorPane.Editor.CaretOffset).LineNumber;
        // .dbg file paths are forward-slashed (cl65 was invoked with e.g. "src/main.c") regardless
        // of this being Windows - normalize so breakpoints resolve against DbgFile.FindAddressForSourceLine.
        var relativePath = Path.GetRelativePath(project.Directory, openPath).Replace('\\', '/');

        var existingIndex = _breakpoints.Breakpoints.FindIndex(b =>
            b.Line == line && string.Equals(b.SourceFile, relativePath, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            _breakpoints.Breakpoints.RemoveAt(existingIndex);
            _host.AppendOutputLine($"Breakpoint removed: {relativePath}:{line}");
        }
        else
        {
            _breakpoints.Breakpoints.Add(new BreakpointEntry(relativePath, line));
            _host.AppendOutputLine($"Breakpoint set: {relativePath}:{line}");
        }

        _breakpoints.Save(project.ResolvedBreakpointsFile);
        RefreshBreakpointHighlights();
        // Otherwise a breakpoint added/removed mid-session has no effect on the already-running
        // VICE instance - checkpoints are only ever set once, at StartDebuggingAsync's own setup.
        Fire(SyncCheckpointsWithViceAsync());
    }

    /// <summary>
    /// Debug > Enable/Disable Breakpoint (Ctrl+F9, as in Visual Studio): turns the caret line's
    /// breakpoint off without losing it (or its condition), or back on. Nothing when the line
    /// has no breakpoint.
    /// </summary>
    public void EnableBreakpointAtCursor() => _host.Guard("Saving breakpoints", EnableBreakpointAtCursorCore);

    private void EnableBreakpointAtCursorCore()
    {
        var project = _workspace.ActiveProject;
        if (project is null || _editorPane.OpenPath is not { } openPath || _editorPane.Editor.Document is null)
            return;

        var line = _editorPane.CaretPosition.Line;
        // Forward slashes, matching the .dbg file - see ToggleBreakpointAtCursorCore.
        var relativePath = Path.GetRelativePath(project.Directory, openPath).Replace('\\', '/');
        var index = _breakpoints.Breakpoints.FindIndex(b =>
            b.Line == line && string.Equals(b.SourceFile, relativePath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        var breakpoint = _breakpoints.Breakpoints[index] with { Enabled = !_breakpoints.Breakpoints[index].Enabled };
        _breakpoints.Breakpoints[index] = breakpoint;
        _host.AppendOutputLine($"Breakpoint {(breakpoint.Enabled ? "enabled" : "disabled")}: {relativePath}:{line}");
        _breakpoints.Save(project.ResolvedBreakpointsFile);
        RefreshBreakpointHighlights();
        Fire(SyncCheckpointsWithViceAsync());
    }

    /// <summary>
    /// Debug > Breakpoint Condition...: sets or clears the condition (VICE monitor syntax) of the
    /// breakpoint on the caret's line, creating the breakpoint first if there isn't one - so a
    /// conditional breakpoint takes one step, not F9 and then this.
    /// </summary>
    public void EditBreakpointConditionAtCursor() => _host.Guard("Saving breakpoints", EditBreakpointConditionAtCursorCore);

    private void EditBreakpointConditionAtCursorCore()
    {
        var project = _workspace.ActiveProject;
        if (project is null || _editorPane.OpenPath is not { } openPath || _editorPane.Editor.Document is null)
            return;

        var line = _editorPane.CaretPosition.Line;
        // Forward slashes, matching the .dbg file - see ToggleBreakpointAtCursorCore.
        var relativePath = Path.GetRelativePath(project.Directory, openPath).Replace('\\', '/');
        var index = _breakpoints.Breakpoints.FindIndex(b =>
            b.Line == line && string.Equals(b.SourceFile, relativePath, StringComparison.OrdinalIgnoreCase));
        var current = index >= 0 ? _breakpoints.Breakpoints[index] : new BreakpointEntry(relativePath, line);

        var dialog = new BreakpointConditionDialog($"{relativePath}:{line}", current.Condition);
        _host.Dialogs.Run(dialog);
        if (dialog.Condition is not { } condition)
            return;

        var updated = current with { Condition = condition.Length == 0 ? null : condition, Enabled = true };
        if (index >= 0)
            _breakpoints.Breakpoints[index] = updated;
        else
            _breakpoints.Breakpoints.Add(updated);
        _host.AppendOutputLine($"Breakpoint set: {updated.Describe()}");

        _breakpoints.Save(project.ResolvedBreakpointsFile);
        RefreshBreakpointHighlights();
        Fire(SyncCheckpointsWithViceAsync());
    }

    public void ShowBreakpointsDialog()
    {
        var project = _workspace.ActiveProject;
        if (project is null)
        {
            _host.AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }

        var dialog = new BreakpointsDialog(_breakpoints, project.ResolvedBreakpointsFile);
        dialog.BreakpointSelected += breakpoint =>
            _host.OpenSymbol((Path.Combine(project.Directory, breakpoint.SourceFile), breakpoint.Line));
        _host.Dialogs.Run(dialog);
        // The dialog mutates the same _breakpoints instance in place (toggle/delete) - refresh in
        // case it changed anything for the currently open file.
        RefreshBreakpointHighlights();
        // Toggling a breakpoint's Enabled flag (or deleting it) here has the exact same "VICE
        // never finds out" gap as ToggleBreakpointAtCursor - see SyncCheckpointsWithViceAsync's
        // own doc comment. The dialog can toggle/delete several entries in one visit, but the
        // resync itself is a full re-sync (delete everything tracked, re-set every still-enabled
        // breakpoint), not incremental, so one call after it closes covers all of them.
        Fire(SyncCheckpointsWithViceAsync());
    }

    /// <summary>
    /// Prompts for a watch expression, resolves it to an address (a matching <see cref="_dbgFile"/>
    /// symbol name if one's loaded, otherwise a raw <c>$hex</c>/decimal address), and adds it to
    /// <see cref="_watches"/>. Shows a placeholder value immediately and, if a session is currently
    /// stopped, kicks off a real read right away rather than waiting for the next step/checkpoint.
    /// </summary>
    public void ShowAddWatchDialog()
    {
        var dialog = new AddWatchDialog();
        _host.Dialogs.Run(dialog);
        if (dialog.Expression is not { } expression)
            return;

        if (!TryResolveWatchAddress(expression, out var address, out var error))
        {
            _host.AppendOutputLine(error);
            return;
        }

        _watches.Add(new WatchEntry(expression, address, dialog.Size));
        _debugPanel.SetWatches(_watches.Select(w => $"{w.Label} (${w.Address:X4}) = ?").ToList());
        Fire(RefreshWatchesIfStoppedAsync());
    }

    public void ClearWatches()
    {
        _watches.Clear();
        _debugPanel.SetWatches([]);
    }

    /// <summary>
    /// Resolves a watch expression to an address: first as a <see cref="_dbgFile"/> symbol name
    /// (matched with or without cc65's leading underscore - the same convention
    /// <see cref="DbgFile.FindEnclosingFunctionName"/> uses), falling back to a literal address
    /// (<c>$hex</c>, <c>0xhex</c>, or plain decimal) so hardware registers (e.g. <c>$D012</c> for
    /// the VIC-II raster line) work even without debug info loaded.
    /// </summary>
    private bool TryResolveWatchAddress(string expression, out ushort address, out string error)
    {
        expression = expression.Trim();

        var symbol = _dbgFile?.Symbols.FirstOrDefault(s =>
            s.Value is not null &&
            (string.Equals(s.Name, expression, StringComparison.Ordinal) ||
             string.Equals(s.Name.TrimStart('_'), expression, StringComparison.Ordinal)));
        if (symbol is not null)
        {
            address = (ushort)symbol.Value!.Value;
            error = "";
            return true;
        }

        var hexText = expression.StartsWith('$') ? expression[1..]
            : expression.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? expression[2..]
            : null;
        if (hexText is not null && ushort.TryParse(hexText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
        {
            address = hexValue;
            error = "";
            return true;
        }

        if (ushort.TryParse(expression, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalValue))
        {
            address = decimalValue;
            error = "";
            return true;
        }

        address = 0;
        error = $"Could not resolve watch \"{expression}\" - enter a known symbol name, or an address as $hex, 0xhex, or decimal.";
        return false;
    }

    /// <summary>Reads every watch's current value from VICE and formats it for display; the
    /// callers put the result on <see cref="_debugPanel"/>.</summary>
    private async Task<List<string>> FormatWatchesAsync(ViceMonitorClient debugClient)
    {
        // A snapshot, not _watches itself - the awaits below give the UI thread a chance to add
        // or clear watches mid-loop, which would otherwise throw "Collection was modified".
        var watches = _watches.ToList();
        var formatted = new List<string>(watches.Count);
        foreach (var watch in watches)
        {
            try
            {
                var bytes = await debugClient.GetMemoryAsync(watch.Address, (ushort)(watch.Address + watch.Size - 1));
                var text = watch.Size == 2 && bytes.Length >= 2
                    ? $"${(ushort)(bytes[0] | (bytes[1] << 8)):X4}"
                    : bytes.Length >= 1 ? $"${bytes[0]:X2}" : "?";
                formatted.Add($"{watch.Label} (${watch.Address:X4}) = {text}");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read memory for watch {Label} (${Address:X4})", watch.Label, watch.Address);
                formatted.Add($"{watch.Label} (${watch.Address:X4}) = ?");
            }
        }
        return formatted;
    }

    /// <summary>Each generated .s file's parsed stack frames, keyed by path - parsed on first stop
    /// in it and kept for the session (cleared when a new session's build replaces them).</summary>
    private readonly Dictionary<string, IReadOnlyList<FunctionFrame>> _assemblyFrames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The Locals table for a stop: the parameters and function-level locals of the C function
    /// the PC is in, read from cc65's software stack. Where each one lives comes from the frame
    /// depth at the stopped instruction in cl65's generated .s (see <see cref="GeneratedAssemblyFrames"/>),
    /// its type from its declaration in the C source (see <see cref="CDeclarations"/>) - cc65's
    /// debug info has neither. Empty outside a project's C code (assembly, the runtime library)
    /// or if anything needed is missing; never throws - locals are a convenience, not worth
    /// failing a stop over.
    /// </summary>
    private async Task<IReadOnlyList<LocalRow>> ReadLocalsAsync(ViceMonitorClient debugClient, RegisterSnapshot registers)
    {
        try
        {
            if (_dbgFile is not { } dbgFile || _workspace.ActiveProject is not { } project || registers["PC"] is not { } pc)
                return [];

            var cLocation = dbgFile.FindProjectSourceLocationForAddress(pc, project.Directory);
            var assemblyLine = dbgFile.FindAssemblyLineForAddress(pc);
            if (cLocation is not { } c || assemblyLine is not { } asm)
                return [];

            var asmPath = Path.Combine(project.Directory, asm.FilePath);
            if (!_assemblyFrames.TryGetValue(asmPath, out var frames))
            {
                if (!File.Exists(asmPath))
                    return [];
                frames = GeneratedAssemblyFrames.Parse(await File.ReadAllTextAsync(asmPath));
                _assemblyFrames[asmPath] = frames;
            }

            var frame = frames.FirstOrDefault(f => asm.Line >= f.FirstLine && asm.Line <= f.LastLine);
            if (frame is null || frame.Symbols.Count == 0)
                return [];
            if (!frame.Reliable || frame.DepthAt(asm.Line) is not { } depth)
                return [new LocalRow("(locals unavailable)", "", $"{frame.Name} uses stack code Tedide can't follow")];

            // cc65's software stack pointer: "sp" in cc65 2.19, renamed "c_sp" in later versions.
            var spSymbol = dbgFile.Symbols.FirstOrDefault(s => s.Name is "sp" or "c_sp" && s.Type == "lab" && s.Value is not null);
            if (spSymbol is null)
                return [];
            var spBytes = await debugClient.GetMemoryAsync((ushort)spSymbol.Value!.Value, (ushort)(spSymbol.Value.Value + 1));
            var sp = (ushort)(spBytes[0] | spBytes[1] << 8);

            var source = await File.ReadAllTextAsync(Path.Combine(project.Directory, c.FilePath));
            var types = CDeclarations.FindTypes(source, frame.Name, frame.Symbols.Select(s => s.Name), c.Line);
            var slots = frame.SlotsAt(depth, sp, types);

            var rows = new List<LocalRow>(slots.Count);
            foreach (var slot in slots)
            {
                var typeText = slot.Type?.Text ?? "?";
                string value;
                if (slot.Address is { } address)
                {
                    var bytes = await debugClient.GetMemoryAsync(address, (ushort)(address + slot.Size - 1));
                    value = LocalValueFormatter.Format(bytes, slot.Type);
                }
                else if (slot.Symbol is { IsParameter: true, Offset: 0 } && registers["A"] is { } a)
                {
                    // cc65's fastcall: the last parameter arrives in A (low) / X (high) and is only
                    // pushed by the function's first instruction.
                    byte[] bytes = slot.Size == 1 ? [(byte)a] : [(byte)a, (byte)(registers["X"] ?? 0)];
                    value = LocalValueFormatter.Format(bytes, slot.Type) + " [in A/X]";
                }
                else
                {
                    value = "(not yet on the stack)";
                }
                rows.Add(new LocalRow(slot.Symbol.IsParameter ? $"{slot.Symbol.Name} (param)" : slot.Symbol.Name, typeText, value));
            }
            return rows;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read locals");
            return [];
        }
    }

    /// <summary>Fire-and-forget refresh for when a watch is added/cleared outside the normal
    /// stop/step flow - a no-op unless a session is both connected and currently stopped (reading
    /// memory while running would race the emulator).</summary>
    private async Task RefreshWatchesIfStoppedAsync()
    {
        if (_debugClient is not { } debugClient || !_isDebugging || !_isStopped)
            return;

        var watchLines = await FormatWatchesAsync(debugClient);
        _debugPanel.SetWatches(watchLines);
    }

    /// <summary>What the Memory and Disassembly tabs and the call stack show for one stop - read
    /// from VICE off the UI thread, then shown by <see cref="ShowDebugViews"/> on it.</summary>
    private sealed record DebugViews(
        IReadOnlyList<CallFrame> CallStack,
        IReadOnlyList<DisassemblyRow> Disassembly,
        string DisassemblyStatus,
        ushort? MemoryAddress,
        byte[] Memory,
        HashSet<int> MemoryChanged);

    /// <summary>Reads everything <see cref="DebugViews"/> holds for the current stop. Never throws:
    /// like the locals, these are extras, not worth failing a stop over.</summary>
    private async Task<DebugViews> ReadDebugViewsAsync(ViceMonitorClient debugClient, RegisterSnapshot registers)
    {
        IReadOnlyList<CallFrame> callStack = [];
        IReadOnlyList<DisassemblyRow> disassembly = [];
        var disassemblyStatus = "No PC to show.";
        try
        {
            callStack = await ReadCallStackAsync(debugClient, registers);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read the call stack");
        }
        try
        {
            if (registers["PC"] is { } pc)
                (disassembly, disassemblyStatus) = await ReadDisassemblyAsync(debugClient, pc);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not disassemble");
            disassemblyStatus = $"Could not read memory around the PC: {ex.Message}";
        }

        var memoryAddress = _memoryAddress;
        byte[] memory = [];
        HashSet<int> changed = [];
        if (memoryAddress is { } address)
        {
            try
            {
                memory = await debugClient.GetMemoryAsync(address, (ushort)Math.Min(0xFFFF, address + MemoryView.ByteCount - 1));
                if (_memorySnapshot is { } previous && previous.Length == memory.Length)
                    changed = MemoryDump.ChangedOffsets(previous, memory);
                _memorySnapshot = memory;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read memory at ${Address:X4}", address);
            }
        }
        return new DebugViews(callStack, disassembly, disassemblyStatus, memoryAddress, memory, changed);
    }

    /// <summary>Shows a stop's <see cref="DebugViews"/>. Must run on the UI thread.</summary>
    private void ShowDebugViews(DebugViews views)
    {
        _debugPanel.SetCallStack(views.CallStack);
        _disassemblyView.Show(views.Disassembly, views.DisassemblyStatus);
        if (views.MemoryAddress is { } address && views.Memory.Length > 0)
        {
            var changedText = views.MemoryChanged.Count == 0 ? "" : $" - {views.MemoryChanged.Count} byte(s) changed since the last stop";
            _memoryView.Show(address, views.Memory, views.MemoryChanged, $"As of this stop{changedText}.");
        }
    }

    /// <summary>
    /// The call stack for a stop, innermost first: where the PC is, then each call site found on
    /// the hardware stack by <see cref="CallStackWalker"/>. Only return addresses into the
    /// program's own read-only segments are considered, and each one's JSR is confirmed by reading
    /// the opcode - one small read per candidate, so a stack full of data doesn't mean 255 reads.
    /// </summary>
    private async Task<IReadOnlyList<CallFrame>> ReadCallStackAsync(ViceMonitorClient debugClient, RegisterSnapshot registers)
    {
        if (_dbgFile is not { } dbgFile || _workspace.ActiveProject is not { } project
            || registers["PC"] is not { } pc || registers["SP"] is not { } sp)
            return [];

        var stack = await debugClient.GetMemoryAsync(0x0100, 0x01FF);
        var isJsr = new Dictionary<ushort, bool>();
        foreach (var site in CallStackWalker.CandidateCallSites(stack, (byte)sp).Where(a => dbgFile.IsInReadOnlySegment(a)).Distinct())
            isJsr[site] = (await debugClient.GetMemoryAsync(site, site))[0] == CallStackWalker.JsrOpcode;

        var frames = new List<CallFrame> { DescribeFrame(dbgFile, project, pc) };
        frames.AddRange(CallStackWalker.Walk(stack, (byte)sp, site => isJsr.GetValueOrDefault(site)).Select(site => DescribeFrame(dbgFile, project, site)));
        return frames;
    }

    /// <summary>A call stack line for <paramref name="address"/>: the function (or, in cc65's
    /// runtime, the nearest label) and the project source line, if it has one.</summary>
    private static CallFrame DescribeFrame(DbgFile dbgFile, TedideProject project, ushort address)
    {
        var name = dbgFile.FindEnclosingFunctionName(address) ?? dbgFile.FindNearestLabel(address) ?? "?";
        return dbgFile.FindProjectSourceLocationForAddress(address, project.Directory) is { } location
            ? new CallFrame(name, $"{DebugPath(project, location.FilePath)}:{location.Line}", Path.Combine(project.Directory, location.FilePath), location.Line)
            : new CallFrame(name, $"${address:X4}", null, 0);
    }

    /// <summary>How many bytes before and after the PC the Disassembly tab reads.</summary>
    private const int DisassemblyBytesBefore = 48, DisassemblyBytesAfter = 96;

    /// <summary>
    /// The instructions around <paramref name="pc"/>, decoded for the project's CPU, each noted
    /// with the label at its address, the label its operand refers to, and the source line where
    /// a new one starts.
    /// </summary>
    private async Task<(IReadOnlyList<DisassemblyRow> Rows, string Status)> ReadDisassemblyAsync(ViceMonitorClient debugClient, ushort pc)
    {
        var start = (ushort)Math.Max(0, pc - DisassemblyBytesBefore);
        var end = (ushort)Math.Min(0xFFFF, pc + DisassemblyBytesAfter);
        var bytes = await debugClient.GetMemoryAsync(start, end);
        var cpu = _workspace.ActiveProject?.ResolvedCc65Cpu ?? "6502";
        var cmos = Disassembler6502.IsCmos(cpu);
        var from = Disassembler6502.FindStartBefore(bytes, pc - start, DisassemblyBytesBefore, cmos);

        var dbgFile = _dbgFile;
        var project = _workspace.ActiveProject;
        (string FilePath, int Line)? lastLocation = null;
        var rows = new List<DisassemblyRow>();
        foreach (var instruction in Disassembler6502.Disassemble(bytes, start, from, maxCount: 48, cmos))
        {
            var notes = new List<string>();
            if (dbgFile?.FindLabelAt(instruction.Address) is { } label)
                notes.Add($"{label}:");
            if (instruction.Target is { } target && instruction.Mnemonic != "bra" && dbgFile?.FindLabelAt(target) is { } targetLabel)
                notes.Add($"-> {targetLabel}");
            if (dbgFile is not null && project is not null
                && dbgFile.FindProjectSourceLocationForAddress(instruction.Address, project.Directory) is { } location
                && location != lastLocation)
            {
                notes.Add($"{DebugPath(project, location.FilePath)}:{location.Line}");
                lastLocation = location;
            }
            rows.Add(new DisassemblyRow(instruction, instruction.Address == pc, string.Join("  ", notes)));
        }
        return (rows, $"PC ${pc:X4} - {cpu} instructions; cycles: * +1 on a page crossing, ** branch +1 taken, +1 more crossing a page.");
    }

    /// <summary>
    /// Memory tab: resolves an address the way Add Watch does (symbol, $hex or decimal) and shows
    /// the memory there - read now if execution is stopped, otherwise at the next stop.
    /// </summary>
    public void ShowMemoryAt(string expression)
    {
        if (!TryResolveWatchAddress(expression, out var address, out var error))
        {
            _memoryView.SetStatus(error);
            return;
        }

        _memoryAddress = address;
        _memorySnapshot = null;
        if (_debugClient is not { } debugClient || !_isDebugging || !_isStopped)
        {
            _memoryView.SetStatus($"${address:X4} will be shown when execution next stops.");
            return;
        }

        Fire(ReadMemoryNowAsync(debugClient, address));
    }

    private async Task ReadMemoryNowAsync(ViceMonitorClient debugClient, ushort address)
    {
        try
        {
            var bytes = await debugClient.GetMemoryAsync(address, (ushort)Math.Min(0xFFFF, address + MemoryView.ByteCount - 1));
            _memorySnapshot = bytes;
            _memoryView.Show(address, bytes, [], "As of this stop.");
        }
        catch (Exception ex)
        {
            _memoryView.SetStatus($"Could not read memory: {ex.Message}");
        }
    }

    /// <summary>Opens the project source line an address belongs to - for a Disassembly row.</summary>
    public void OpenSourceForAddress(ushort address)
    {
        if (_dbgFile is not { } dbgFile || _workspace.ActiveProject is not { } project)
            return;
        if (dbgFile.FindProjectSourceLocationForAddress(address, project.Directory) is { } location)
            _host.OpenSymbol((Path.Combine(project.Directory, location.FilePath), location.Line));
        else
            _host.AppendOutputLine($"${address:X4} has no source line in this project.");
    }

    /// <summary>
    /// Builds the active project (if needed), launches it in VICE with the binary monitor enabled,
    /// connects <see cref="_debugClient"/>, sets every enabled breakpoint (resolved to an address
    /// via <see cref="_dbgFile"/>), and starts it running. Requires <see cref="TedideProject.GenerateDebugInfo"/>
    /// to be on - without it there's no .dbg file to resolve breakpoints/addresses against.
    /// Every caller fires this and forgets it, so any exception is caught and reported here and
    /// whatever was already set up is torn down - otherwise e.g. VICE not being installed would
    /// leave the Debug tab showing a half-started session with no error at all.
    /// </summary>
    public async Task StartDebuggingAsync()
    {
        try
        {
            await StartDebuggingCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error starting debug session");
            _host.AppendOutputLine($"Could not start debugging: {ex.Message}");
            await EndDebugSessionAsync();
        }
    }

    private async Task StartDebuggingCoreAsync()
    {
        var project = _workspace.ActiveProject;
        if (project is null)
        {
            _host.AppendOutputLine("No project loaded. Use File > Open Project or File > New Project first.");
            return;
        }
        if (!_host.CheckStartupProjectRuns("debug"))
            return;
        if (!project.GenerateDebugInfo)
        {
            _host.Dialogs.ErrorQuery("Debug Info Required",
                "\"Generate debug info\" is off for this project.\n" +
                "Enable it on the Linker tab of Project Settings, then rebuild before starting a debug session.",
                ["OK"]);
            return;
        }
        if (_isDebugging)
        {
            _host.AppendOutputLine("Already debugging - use Debug > Stop Debugging first.");
            return;
        }

        // Switch to the Debug tab immediately so its locals/call stack panel is what the user sees
        // as the session comes up, rather than whatever tab (Output/Error List/Symbols) happened
        // to be selected before.
        _host.ShowDebugTab();

        var buildResult = await _host.BuildActiveProjectAsync();
        if (buildResult is not { Succeeded: true })
            return;

        if (!File.Exists(project.ResolvedDebugInfoFile))
        {
            _host.AppendOutputLine("Build succeeded but no debug info file was produced.");
            return;
        }
        _dbgFile = DbgFile.Parse(File.ReadAllText(project.ResolvedDebugInfoFile));
        _assemblyFrames.Clear(); // this build's generated .s files replace the last session's

        // Application.Invoke, not AppendOutputLine directly: Process.OutputDataReceived/
        // ErrorDataReceived (what ViceEmulator.Launch's onOutputLine ultimately wraps) fire on a
        // thread-pool thread, not the UI thread - confirmed via a real crash this line caused
        // ("Collection was modified; enumeration operation may not execute" inside TextView's own
        // draw, racing OutputView._lines against the UI thread's concurrent draw-time enumeration
        // of it). RunActiveProjectAsync's own _host.Vice.Launch call already gets this right.
        var viceProcess = _host.Vice.Launch(project, line => Application.Invoke(() => _host.AppendOutputLine(line)), enableBinaryMonitor: true);

        _host.SetDebugStatus("Connecting to VICE...");
        // VICE needs a moment to start listening on its binary monitor port after the process
        // starts - retry rather than failing on the first attempt. A fresh client per attempt: a
        // TcpClient whose connect has failed isn't reliably reusable. And give up straight away
        // if VICE has already exited (e.g. it rejected a ROM or command-line option) rather than
        // spending the whole retry budget knocking on a port nothing will ever open.
        for (var attempt = 0; attempt < 20 && _debugClient is null && !viceProcess.HasExited; attempt++)
        {
            var client = CreateDebugClient();
            try
            {
                await client.ConnectAsync();
                _debugClient = client;
                Log.Debug("Debug start: connected to VICE on attempt {Attempt}", attempt + 1);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                Log.Debug("Debug start: connect attempt {Attempt} failed: {Reason}", attempt + 1, ex.Message);
                await client.DisposeAsync();
                await Task.Delay(250);
            }
        }
        if (_debugClient is null)
        {
            var reason = viceProcess.HasExited
                ? $"VICE exited during startup (exit code {viceProcess.ExitCode}) - see its output above."
                : "Could not connect to VICE's binary monitor - is VICE installed and did it launch correctly?";
            _host.AppendOutputLine(reason);
            _host.SetDebugStatus(null);
            return;
        }

        _isDebugging = true;
        // Read-only for the whole session, in every tab - editing source while the compiled binary
        // it no longer matches is running would be misleading (BuildActiveProjectAsync above has
        // already saved everything).
        _editorPane.ReadOnly = true;

        // Show the C source containing main() as the session comes up, before anything actually
        // runs - the same _main label every C program has, resolved back to its source location
        // the same way a checkpoint hit resolves the PC (see DbgFile.FindSourceLocationForAddress).
        var mainSymbol = _dbgFile.Symbols.FirstOrDefault(s => s.Name == "_main" && s.Type == "lab");
        Log.Debug("Debug start: _main at {Address}", mainSymbol?.Value);
        if (mainSymbol is { Value: { } mainAddress }
            && _dbgFile.FindSourceLocationForAddress(mainAddress) is { } mainLocation)
        {
            Log.Debug("Debug start: main() is at {File}:{Line}", mainLocation.FilePath, mainLocation.Line);
            var mainPath = Path.Combine(project.Directory, mainLocation.FilePath);
            _host.OpenSymbol((mainPath, mainLocation.Line));
            _host.CenterEditorOnLine(mainPath, mainLocation.Line);
        }

        Log.Debug("Debug start: setting checkpoints");
        var dbgFile = _dbgFile;
        await _checkpoints.ArmAsync(_debugClient, _breakpoints.Breakpoints,
            b => dbgFile.FindAddressForSourceLine(b.SourceFile, b.Line), _host.AppendOutputLine);

        Log.Debug("Debug start: checkpoints set, continuing");
        _host.SetDebugStatus("Running...");
        await _debugClient.ContinueAsync();
    }

    /// <summary>A new, not-yet-connected monitor client with this shell's event handlers attached.</summary>
    private ViceMonitorClient CreateDebugClient()
    {
        var client = new ViceMonitorClient();
        client.CheckpointHit += OnCheckpointHit;
        client.EventHandlerFailed += ex => Log.Error(ex, "Debug event handler failed");
        // VICE closed (or crashed) under a live session: end it, so the editor becomes editable
        // again and the Debug panel stops claiming a session exists. Checked on the UI thread
        // against the *current* client, so a stale client from an earlier session does nothing.
        client.Disconnected += () => Application.Invoke(() =>
        {
            if (_debugClient != client || !_isDebugging)
                return;
            _host.AppendOutputLine("VICE closed the debugging connection - debug session ended.");
            Fire(EndDebugSessionAsync());
        });
        client.Resumed += pc => Application.Invoke(() =>
        {
            Log.Debug("Resumed event: PC={PC:X4}, _isStepping={IsStepping}", pc, _isStepping);
            // Stepping resumes and re-halts the CPU just like Continue does, so it raises this same
            // unsolicited Resumed event - without this guard, its queued "Running..." update could
            // land after StepDebuggingAsync's own "Stopped at ..." one, clobbering the status and
            // un-highlighting the line it had just moved to. (ShowStoppedAt also re-asserts
            // _isStopped, for a Resumed that slips in just after _isStepping is cleared.)
            if (_isStepping)
                return;

            _isStopped = false;
            _host.SetDebugLine(null);
            _host.SetDebugStatus("Running...");
            _editorPane.Editor.SetNeedsDraw();
        });
        return client;
    }

    /// <summary>
    /// Brings VICE's checkpoints in line with the breakpoints after one is added, removed, enabled
    /// or disabled mid-session - see <see cref="CheckpointSet.ResyncAsync"/>. Nothing outside a
    /// session.
    /// </summary>
    public async Task SyncCheckpointsWithViceAsync()
    {
        if (_debugClient is not { } debugClient || _dbgFile is not { } dbgFile || !_isDebugging)
            return;

        try
        {
            // Re-checked under the CheckpointSet's lock: a Stop Debugging that ran while this
            // waited has already deleted every checkpoint and disposed this client.
            await _checkpoints.ResyncAsync(debugClient, _breakpoints.Breakpoints,
                b => dbgFile.FindAddressForSourceLine(b.SourceFile, b.Line), _host.AppendOutputLine,
                stillCurrent: () => _isDebugging && _debugClient == debugClient);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error re-syncing breakpoints with VICE");
            _host.AppendOutputLine($"Error updating breakpoints in the running debug session: {ex.Message}");
        }
    }

    /// <summary>Builds a "Stopped [in {function}] at {where}" status string, prepending the
    /// enclosing function name (via <see cref="_dbgFile"/>) when it resolves - e.g. code with no
    /// debug info at all (cc65's own runtime library) has no scope info, so this falls back to
    /// just "Stopped at {where}".</summary>
    /// <summary>
    /// A source path from the debug info, for display: as it is when relative (the startup project's
    /// own files), else relative to the startup project - a library project's sources are compiled by
    /// full path (see Cc65Toolchain.BuildCompileSteps), which made every stop in one a whole line of
    /// C:\Users\... . "../Gfx/src/gfx.c" matches how the breakpoint list writes it.
    /// </summary>
    private static string DebugPath(TedideProject project, string path) =>
        Path.IsPathRooted(path) ? Path.GetRelativePath(project.Directory, path).Replace('\\', '/') : path;

    private string FunctionAwareStoppedAt(ushort? pc, string where)
    {
        var function = pc is { } pcValue ? _dbgFile?.FindEnclosingFunctionName(pcValue) : null;
        return function is { } name ? $"Stopped in {name} at {where}" : $"Stopped at {where}";
    }

    /// <summary>
    /// Fires whenever VICE stops at a checkpoint - reads registers, resolves the PC back to a
    /// source location (<see cref="_dbgFile"/>), and jumps the editor there. Raised on
    /// <see cref="ViceMonitorClient"/>'s read-loop thread, so the work is posted to the UI thread
    /// with Application.Invoke, which returns at once - the read loop must stay free to answer the
    /// register and memory requests that follow. Once there, the awaits come back to the UI thread
    /// (see <see cref="UiSynchronizationContext"/>).
    /// </summary>
    private void OnCheckpointHit(CheckpointHitEventArgs args)
    {
        Log.Debug("CheckpointHit event: checkpoint #{Number}", args.Checkpoint.Number);
        Application.Invoke(async () =>
        {
            try
            {
                _isStopped = true;
                if (_debugClient is null)
                    return;

                var registers = await _debugClient.GetRegistersAsync();
                var watchLines = await FormatWatchesAsync(_debugClient);
                var locals = await ReadLocalsAsync(_debugClient, registers);
                var views = await ReadDebugViewsAsync(_debugClient, registers);

                // "PC" is VICE's register name for the 6502 program counter on the main memspace -
                // confirmed against a live VICE 3.9 instance (its ids are assigned dynamically per
                // the binary monitor protocol docs, but this name was stable).
                ShowStoppedAt(registers, watchLines, locals, registers["PC"], $" (checkpoint #{args.Checkpoint.Number})");
                ShowDebugViews(views);
                Log.Debug("CheckpointHit done");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling checkpoint hit");
                _host.AppendOutputLine($"Error handling checkpoint hit: {ex.Message}");
            }
        });
    }

    /// <summary>F5, as in Visual Studio: Start Debugging, or Continue when stopped at a breakpoint
    /// (nothing while it's running).</summary>
    public Task StartOrContinueDebuggingAsync() =>
        !_isDebugging ? StartDebuggingAsync()
        : _isStopped ? ContinueDebuggingAsync()
        : Task.CompletedTask;

    public async Task ContinueDebuggingAsync()
    {
        if (_debugClient is null || !_isDebugging)
            return;

        _isStopped = false;
        _host.SetDebugLine(null);
        _host.SetDebugStatus("Running...");
        _editorPane.Editor.SetNeedsDraw();
        await _debugClient.ContinueAsync();
    }

    /// <summary>
    /// Updates the Debug panel and editor to show where execution has stopped: registers, watches,
    /// the source line <paramref name="pc"/> resolves to (opened, centered and highlighted), and a
    /// "Stopped [in function] at file:line" status (in the window title), with
    /// <paramref name="statusSuffix"/> appended (e.g. which checkpoint fired). Falls back to a bare
    /// address when the PC has no source line. Must run on the UI thread - it touches
    /// Editor/TextDocument state, which enforces single-thread ownership.
    /// </summary>
    private void ShowStoppedAt(RegisterSnapshot registers, List<string> watchLines, IReadOnlyList<LocalRow> locals, ushort? pc, string statusSuffix)
    {
        _isStopped = true;
        _debugPanel.SetRegisters(registers);
        _debugPanel.SetWatches(watchLines);
        _debugPanel.SetLocals(locals);

        var project = _workspace.ActiveProject;
        // Project files only: a line record from cc65's runtime library can't be opened, and used
        // to move the current-line highlight to its line number in whatever file was open.
        var location = pc is { } pcForLookup && project is not null
            ? _dbgFile?.FindProjectSourceLocationForAddress(pcForLookup, project.Directory)
            : null;
        Log.Debug("Stopped: PC={PC:X4}, location={Location}",
            pc, location is { } loc ? $"{loc.FilePath}:{loc.Line}" : "(unresolved)");

        string status;
        if (project is not null && location is { } resolved)
        {
            var resolvedPath = Path.Combine(project.Directory, resolved.FilePath);
            _host.OpenSymbol((resolvedPath, resolved.Line));
            _host.CenterEditorOnLine(resolvedPath, resolved.Line);
            _host.SetDebugLine((resolvedPath, resolved.Line));
            status = FunctionAwareStoppedAt(pc, $"{DebugPath(project, resolved.FilePath)}:{resolved.Line}") + statusSuffix;
        }
        else
        {
            _host.SetDebugLine(null);
            status = (pc is { } pcv ? $"Stopped at ${pcv:X4}" : "Stopped.") + statusSuffix;
        }
        _host.SetDebugStatus(status);
        _editorPane.Editor.SetNeedsDraw();
    }

    /// <summary>
    /// Step Over (F10) or Step Into (F7): steps by source line - see <see cref="SourceStepper"/> -
    /// then shows where it stopped.
    /// </summary>
    public async Task StepDebuggingAsync(bool stepInto)
    {
        if (_debugClient is not { } debugClient || _dbgFile is not { } dbgFile || !_isDebugging || !_isStopped
            || _workspace.ActiveProject is not { } project)
            return;

        // Each single step raises VICE's Resumed event; while this is set, the Resumed handler
        // leaves the UI alone - this method owns it until the step is over.
        _isStepping = true;
        try
        {
            var startPc = (await debugClient.GetRegistersAsync())["PC"] ?? 0;
            var step = await SourceStepper.StepAsync(debugClient, startPc,
                address => dbgFile.FindProjectSourceLocationForAddress(address, project.Directory), stepInto);

            var registers = await debugClient.GetRegistersAsync();
            var watchLines = await FormatWatchesAsync(debugClient);
            var locals = await ReadLocalsAsync(debugClient, registers);
            var views = await ReadDebugViewsAsync(debugClient, registers);
            ShowStoppedAt(registers, watchLines, locals, registers["PC"],
                step.ReachedNewLine ? "" : $" (step limit of {SourceStepper.MaxInstructions} instructions reached)");
            ShowDebugViews(views);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while stepping");
            _host.AppendOutputLine($"Error while stepping: {ex.Message}");
        }
        finally
        {
            _isStepping = false;
        }
    }

    public async Task StopDebuggingAsync()
    {
        if (_debugClient is null)
            return;

        await EndDebugSessionAsync();
    }

    /// <summary>
    /// Tears down a debug session, however far it got - shared by Stop Debugging and by
    /// <see cref="StartDebuggingAsync"/>'s own failure path, where there may be no connected
    /// client yet at all.
    /// </summary>
    private async Task EndDebugSessionAsync()
    {
        // Cleared first so any SyncCheckpointsWithViceAsync already queued behind the lock
        // below bails out on its own re-check instead of re-arming checkpoints afterward.
        _isDebugging = false;
        _isStopped = false;

        if (_debugClient is { } debugClient)
        {
            // Deleted before resuming, not just forgotten - VICE keeps its checkpoints after the
            // connection closes, so the now-detached program would otherwise still halt at every
            // breakpoint, dropping the user into VICE's own monitor with no debugger attached.
            await _checkpoints.ClearAsync(debugClient);

            try { await debugClient.ContinueAsync(); }
            catch { /* VICE may already be gone - fine, we're tearing down the connection either way. */ }

            await debugClient.DisposeAsync();
        }

        _debugClient = null;
        _dbgFile = null;
        // A watch's address was resolved against this session's own _dbgFile - stale the moment
        // it's gone (a rebuild can shift where a symbol ends up), so watches are re-entered per
        // session rather than carried forward, same as WatchEntry's own doc comment says.
        _watches.Clear();
        _host.SetDebugLine(null);
        _host.SetDebugStatus(null);
        _debugPanel.SetRegisters(null);
        _debugPanel.SetWatches([]);
        _debugPanel.SetLocals([]);
        _debugPanel.SetCallStack([]);
        _disassemblyView.Show([], "Start debugging to see the code around the PC.");
        _memoryView.Show(0, [], [], "Start debugging, then enter a symbol or $address.");
        _memorySnapshot = null;
        // EditorPane keeps the editor read-only anyway while no file is open.
        _editorPane.ReadOnly = false;
        _editorPane.Editor.SetNeedsDraw();
    }
}
