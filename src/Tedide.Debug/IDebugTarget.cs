namespace Tedide.Debug;

/// <summary>
/// What <see cref="SourceStepper"/> and <see cref="CheckpointSet"/> need from a running program:
/// single-stepping and setting breakpoints. <see cref="ViceMonitorClient"/> is the real one; the
/// interface is what lets those two be tested without VICE.
/// </summary>
public interface IDebugTarget
{
    /// <summary>Executes one instruction - a whole subroutine call when
    /// <paramref name="stepOverSubroutines"/> - and returns the PC it stopped at.</summary>
    Task<ushort> StepAsync(bool stepOverSubroutines, CancellationToken cancellationToken = default);

    /// <summary>Runs until the next RTS and returns the PC it stopped at.</summary>
    Task<ushort> ExecuteUntilReturnAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets an execution breakpoint at <paramref name="address"/>, returning its number.</summary>
    Task<uint> SetBreakpointAsync(ushort address, CancellationToken cancellationToken = default);

    /// <summary>Makes breakpoint <paramref name="checkpointNumber"/> conditional. Throws
    /// <see cref="ViceMonitorException"/> if the condition is rejected.</summary>
    Task SetConditionAsync(uint checkpointNumber, string condition, CancellationToken cancellationToken = default);

    Task DeleteCheckpointAsync(uint checkpointNumber, CancellationToken cancellationToken = default);
}
