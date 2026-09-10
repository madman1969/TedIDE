namespace Tedide.Debug;

/// <summary>Raised by <see cref="ViceMonitorClient.CheckpointHit"/> when a checkpoint stops
/// execution - VICE reports this by resending the same "Checkpoint info" response used for
/// <see cref="ViceMonitorClient.SetCheckpointAsync"/>'s own reply, but unsolicited (request id
/// 0xFFFFFFFF instead of echoing a request Tedide sent).</summary>
public sealed record CheckpointHitEventArgs(CheckpointInfo Checkpoint);
