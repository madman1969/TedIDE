namespace Tedide.Debug;

/// <summary>
/// A point-in-time read of every register VICE's binary monitor reported, keyed by name (e.g. "A",
/// "X", "Y", "SP", "PC", "FL" on the C64/6502 main memspace - the exact set of names is whatever
/// <see cref="ViceMonitorClient.GetAvailableRegistersAsync"/> returned for this session, not a fixed
/// list Tedide assumes up front, since VICE assigns register ids/names dynamically).
/// </summary>
public sealed class RegisterSnapshot(IReadOnlyDictionary<string, ushort> valuesByName)
{
    public IEnumerable<string> RegisterNames => valuesByName.Keys;

    public ushort? this[string name] => valuesByName.TryGetValue(name, out var value) ? value : null;
}
