namespace Tedide.Core.Debugging;

/// <summary>One 16-byte row of the Memory tab: its address, each byte as two hex digits, and
/// the bytes as text.</summary>
public sealed record MemoryRow(ushort Address, string[] Hex, string Text);

/// <summary>Formats memory read from VICE for the debugger's Memory tab.</summary>
public static class MemoryDump
{
    public const int BytesPerRow = 16;

    /// <summary>Splits <paramref name="bytes"/>, read from <paramref name="startAddress"/> on, into
    /// rows of <see cref="BytesPerRow"/>. A short last row is padded with blanks.</summary>
    public static List<MemoryRow> Rows(ushort startAddress, ReadOnlySpan<byte> bytes)
    {
        var rows = new List<MemoryRow>();
        for (var offset = 0; offset < bytes.Length; offset += BytesPerRow)
        {
            var hex = new string[BytesPerRow];
            var text = new char[BytesPerRow];
            for (var i = 0; i < BytesPerRow; i++)
            {
                var present = offset + i < bytes.Length;
                hex[i] = present ? bytes[offset + i].ToString("X2") : "  ";
                text[i] = present ? Printable(bytes[offset + i]) : ' ';
            }
            rows.Add(new MemoryRow((ushort)(startAddress + offset), hex, new string(text)));
        }
        return rows;
    }

    /// <summary>A byte as a character for the text column: printable ASCII as itself, PETSCII's
    /// shifted letters ($C1-$DA) as capitals, everything else as ".". Commodore text in memory is
    /// usually PETSCII (or screen codes, which this doesn't attempt), so this is a rough guide.</summary>
    public static char Printable(byte value) => value switch
    {
        >= 0x20 and < 0x7F => (char)value,
        >= 0xC1 and <= 0xDA => (char)(value - 0x80),
        _ => '.',
    };

    /// <summary>The offsets at which <paramref name="current"/> differs from <paramref name="previous"/>
    /// (both read from the same start address) - for highlighting what changed since the last stop.</summary>
    public static HashSet<int> ChangedOffsets(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
    {
        var changed = new HashSet<int>();
        for (var i = 0; i < Math.Min(previous.Length, current.Length); i++)
            if (previous[i] != current[i])
                changed.Add(i);
        return changed;
    }
}
