namespace Tedide.Debug;

/// <summary>Which memory accesses a checkpoint stops on - VICE's binary monitor protocol treats
/// this as a bit combination (e.g. Load|Store for "either read or write"), not a single choice.</summary>
[Flags]
public enum ViceCheckpointOperation : byte
{
    Load = 0x01,
    Store = 0x02,
    Exec = 0x04,
}

/// <summary>A breakpoint/watchpoint as VICE's binary monitor reports it - see the "Checkpoint info"
/// response body in <see cref="ViceMonitorProtocol"/>'s doc comment.</summary>
public sealed record CheckpointInfo(
    uint Number,
    bool CurrentlyHit,
    ushort StartAddress,
    ushort EndAddress,
    bool StopWhenHit,
    bool Enabled,
    ViceCheckpointOperation Operation,
    bool Temporary,
    uint HitCount,
    uint IgnoreCount,
    bool HasCondition,
    byte Memspace);
