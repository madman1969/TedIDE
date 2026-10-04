using Tedide.Core;

namespace Tedide.Debug;

/// <summary>
/// The breakpoints armed in a debug session: each enabled breakpoint's checkpoint number, so it
/// can be deleted again. Every pass is serialized - toggling two breakpoints in quick succession
/// otherwise runs two passes interleaved across their awaits, one clearing the numbers while the
/// other is still setting them.
/// </summary>
public sealed class CheckpointSet
{
    private readonly Dictionary<BreakpointEntry, uint> _numbers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Each armed breakpoint's checkpoint number.</summary>
    public IReadOnlyDictionary<BreakpointEntry, uint> Numbers => _numbers;

    /// <summary>Sets a checkpoint for every enabled breakpoint, at session start.</summary>
    /// <param name="resolveAddress">The address a breakpoint's source line compiled to, or null.</param>
    /// <param name="report">Told about a breakpoint that couldn't be armed, in words for the user.</param>
    public async Task ArmAsync(IDebugTarget target, IEnumerable<BreakpointEntry> breakpoints,
        Func<BreakpointEntry, long?> resolveAddress, Action<string> report)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SetAllAsync(target, breakpoints, resolveAddress, report).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Brings the target's checkpoints in line with <paramref name="breakpoints"/> after one was
    /// added, removed, enabled or disabled mid-session - otherwise VICE never finds out, and
    /// execution still stops at a deleted breakpoint. Deletes every checkpoint set before and
    /// re-sets the enabled ones, rather than diffing: the cost is nothing for a handful.
    /// </summary>
    /// <param name="stillCurrent">Re-checked once the lock is held: a session that ended while this
    /// waited has already deleted its checkpoints, and mustn't have them re-armed.</param>
    public async Task ResyncAsync(IDebugTarget target, IEnumerable<BreakpointEntry> breakpoints,
        Func<BreakpointEntry, long?> resolveAddress, Action<string> report, Func<bool> stillCurrent)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!stillCurrent())
                return;
            await DeleteAllAsync(target).ConfigureAwait(false);
            await SetAllAsync(target, breakpoints, resolveAddress, report).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Deletes every checkpoint set, at session end. VICE keeps its checkpoints after the
    /// connection closes, so a detached program would otherwise still halt at each one.</summary>
    public async Task ClearAsync(IDebugTarget target)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DeleteAllAsync(target).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SetAllAsync(IDebugTarget target, IEnumerable<BreakpointEntry> breakpoints,
        Func<BreakpointEntry, long?> resolveAddress, Action<string> report)
    {
        // A snapshot: the caller's list can change while this waits on the target.
        foreach (var breakpoint in breakpoints.Where(b => b.Enabled).ToList())
        {
            if (resolveAddress(breakpoint) is not { } address)
            {
                report($"Could not resolve breakpoint {breakpoint.SourceFile}:{breakpoint.Line} to an address - it may be on a line with no compiled code.");
                continue;
            }

            var number = await target.SetBreakpointAsync((ushort)address).ConfigureAwait(false);
            _numbers[breakpoint] = number;
            if (!breakpoint.HasCondition)
                continue;
            try
            {
                await target.SetConditionAsync(number, breakpoint.Condition!.Trim()).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ViceMonitorException or ArgumentException)
            {
                // An unconditional stop where a conditional one was asked for would be a surprise
                // mid-run - so the breakpoint sits this session out instead.
                await target.DeleteCheckpointAsync(number).ConfigureAwait(false);
                _numbers.Remove(breakpoint);
                report($"VICE rejected the condition \"{breakpoint.Condition}\" on {breakpoint.SourceFile}:{breakpoint.Line}, so that breakpoint is off for this session. "
                    + "Use VICE monitor syntax, e.g. A == $05 or @cpu:$d020 == $0e.");
            }
        }
    }

    /// <summary>Individual failures are skipped: VICE may already be gone, or have dropped a
    /// checkpoint itself.</summary>
    private async Task DeleteAllAsync(IDebugTarget target)
    {
        foreach (var number in _numbers.Values.ToList())
        {
            try
            {
                await target.DeleteCheckpointAsync(number).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Nothing to do - it's gone either way.
            }
        }
        _numbers.Clear();
    }
}
