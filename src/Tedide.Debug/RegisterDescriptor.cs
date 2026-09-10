namespace Tedide.Debug;

/// <summary>
/// One CPU register VICE's binary monitor knows about, as reported by the "Registers available"
/// command - the protocol assigns register ids dynamically per session rather than a fixed table
/// (the VICE Manual itself says not to rely on ids being consistent across versions/targets), so
/// resolving a name like "A" or "PC" to its id means fetching this list first - see
/// <see cref="ViceMonitorClient.GetAvailableRegistersAsync"/>.
/// </summary>
public sealed record RegisterDescriptor(byte Id, string Name, byte SizeBits);
