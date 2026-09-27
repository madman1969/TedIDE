using System.Globalization;

namespace Tedide.Core.Debugging;

/// <summary>Formats a local variable's bytes (little-endian, as the 6502 stores them) for the
/// Debug panel: pointers as an address, numbers in decimal with the hex alongside, chars with
/// their printable character, and anything it can't size as raw hex bytes.</summary>
public static class LocalValueFormatter
{
    public static string Format(ReadOnlySpan<byte> bytes, CVariableType? type)
    {
        if (bytes.Length == 0)
            return "?";

        if (type is { IsPointer: true } && bytes.Length == 2)
            return $"${Word(bytes):X4}";

        var signed = type?.IsSigned ?? false;
        switch (bytes.Length)
        {
            case 1:
            {
                long value = signed ? (sbyte)bytes[0] : bytes[0];
                var character = type is { IsChar: true } && bytes[0] is >= 0x20 and < 0x7F ? $" '{(char)bytes[0]}'" : "";
                return $"{value.ToString(CultureInfo.InvariantCulture)}{character} (${bytes[0]:X2})";
            }
            case 2:
            {
                var word = Word(bytes);
                long value = signed ? (short)word : word;
                return $"{value.ToString(CultureInfo.InvariantCulture)} (${word:X4})";
            }
            case 4:
            {
                var dword = (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24);
                long value = signed ? (int)dword : dword;
                return $"{value.ToString(CultureInfo.InvariantCulture)} (${dword:X8})";
            }
            default:
                return string.Join(" ", bytes.ToArray().Select(b => $"{b:X2}"));
        }
    }

    private static ushort Word(ReadOnlySpan<byte> bytes) => (ushort)(bytes[0] | bytes[1] << 8);
}
