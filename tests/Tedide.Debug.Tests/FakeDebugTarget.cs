using Tedide.Debug;

namespace Tedide.Debug.Tests;

/// <summary>
/// A scripted <see cref="IDebugTarget"/>: each step or run-until-return hands back the next PC
/// from its script, and every call is recorded.
/// </summary>
internal sealed class FakeDebugTarget : IDebugTarget
{
    private readonly Queue<ushort> _stepPcs;
    private readonly Queue<ushort> _returnPcs;
    private uint _nextNumber = 1;

    public FakeDebugTarget(IEnumerable<ushort>? stepPcs = null, IEnumerable<ushort>? returnPcs = null)
    {
        _stepPcs = new Queue<ushort>(stepPcs ?? []);
        _returnPcs = new Queue<ushort>(returnPcs ?? []);
    }

    public List<string> Calls { get; } = [];

    /// <summary>Whether each call yields before answering, as a real round trip to VICE does -
    /// so overlapping callers really interleave.</summary>
    public bool Yields { get; init; }

    /// <summary>Conditions to reject, as VICE does one it can't parse.</summary>
    public HashSet<string> RejectedConditions { get; } = [];

    public Task<ushort> StepAsync(bool stepOverSubroutines, CancellationToken cancellationToken = default)
    {
        Calls.Add(stepOverSubroutines ? "step over" : "step into");
        return Task.FromResult(_stepPcs.Dequeue());
    }

    public Task<ushort> ExecuteUntilReturnAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("until return");
        return Task.FromResult(_returnPcs.Dequeue());
    }

    public async Task<uint> SetBreakpointAsync(ushort address, CancellationToken cancellationToken = default)
    {
        if (Yields)
            await Task.Yield();
        var number = _nextNumber++;
        Calls.Add($"set #{number} at ${address:X4}");
        return number;
    }

    public Task SetConditionAsync(uint checkpointNumber, string condition, CancellationToken cancellationToken = default)
    {
        Calls.Add($"condition #{checkpointNumber}: {condition}");
        return RejectedConditions.Contains(condition)
            ? Task.FromException(new ArgumentException($"VICE couldn't parse {condition}"))
            : Task.CompletedTask;
    }

    public async Task DeleteCheckpointAsync(uint checkpointNumber, CancellationToken cancellationToken = default)
    {
        if (Yields)
            await Task.Yield();
        Calls.Add($"delete #{checkpointNumber}");
    }
}
