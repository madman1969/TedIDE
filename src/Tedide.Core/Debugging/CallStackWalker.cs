namespace Tedide.Core.Debugging;

/// <summary>
/// Rebuilds a call stack from the 6502 hardware stack. cc65 keeps no frame records - C locals and
/// arguments live on its own software stack - but every JSR still pushes its return address (the
/// address of its own last byte) onto page 1. So reading up from the stack pointer, a byte pair
/// that points just past a JSR instruction is taken as a return address, and that JSR as a call
/// site. Interrupt frames and data the code happened to push can occasionally pass for one, so
/// this is the same heuristic a 6502 monitor's own "backtrace" uses, not a guarantee.
/// </summary>
public static class CallStackWalker
{
    public const byte JsrOpcode = 0x20;

    /// <summary>
    /// Every position above the stack pointer that could hold a return address, with the call site
    /// it would imply (the JSR's own address: pushed value - 2), innermost first. The caller reads
    /// the opcode at each call site it cares about - typically only those inside the program's code
    /// - and passes the answers to <see cref="Walk"/>.
    /// </summary>
    /// <param name="stackPage">The 256 bytes of $0100-$01FF.</param>
    /// <param name="sp">The stack pointer: the next free slot, so the newest byte is at sp + 1.</param>
    public static IEnumerable<ushort> CandidateCallSites(ReadOnlySpan<byte> stackPage, byte sp)
    {
        var sites = new List<ushort>();
        for (var i = sp + 1; i + 1 < stackPage.Length; i++)
            sites.Add((ushort)((stackPage[i] | (stackPage[i + 1] << 8)) - 2));
        return sites;
    }

    /// <summary>
    /// The call sites on the stack, innermost (most recent call) first: each byte pair from just
    /// above <paramref name="sp"/> upward that points past a JSR, per <paramref name="isJsrAt"/>.
    /// A matched pair is consumed whole, so its high byte can't also start another match.
    /// </summary>
    public static List<ushort> Walk(ReadOnlySpan<byte> stackPage, byte sp, Func<ushort, bool> isJsrAt, int maxFrames = 32)
    {
        var sites = new List<ushort>();
        for (var i = sp + 1; i + 1 < stackPage.Length && sites.Count < maxFrames;)
        {
            var site = (ushort)((stackPage[i] | (stackPage[i + 1] << 8)) - 2);
            if (isJsrAt(site))
            {
                sites.Add(site);
                i += 2;
            }
            else
            {
                i++;
            }
        }
        return sites;
    }
}
