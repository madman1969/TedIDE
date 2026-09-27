using Tedide.Core.Debugging;

namespace Tedide.Core.Tests.Debugging;

public class LocalValueFormatterTests
{
    private static CVariableType Type(int? size, bool pointer = false, bool signed = false, bool isChar = false) =>
        new("t", size, pointer, signed, isChar);

    [Theory]
    [InlineData(new byte[] { 0x25, 0x00 }, false, "37 ($0025)")]
    [InlineData(new byte[] { 0xFF, 0xFF }, true, "-1 ($FFFF)")]
    [InlineData(new byte[] { 0xFF, 0xFF }, false, "65535 ($FFFF)")]
    public void Words_ShowDecimalAndHex_RespectingSignedness(byte[] bytes, bool signed, string expected)
    {
        Assert.Equal(expected, LocalValueFormatter.Format(bytes, Type(2, signed: signed)));
    }

    [Fact]
    public void Pointers_ShowAsAnAddress()
    {
        Assert.Equal("$C000", LocalValueFormatter.Format([0x00, 0xC0], Type(2, pointer: true)));
    }

    [Fact]
    public void Chars_ShowTheirPrintableCharacter()
    {
        Assert.Equal("65 'A' ($41)", LocalValueFormatter.Format([0x41], Type(1, isChar: true)));
        Assert.Equal("7 ($07)", LocalValueFormatter.Format([0x07], Type(1, isChar: true)));
    }

    [Fact]
    public void SignedBytesAndLongs()
    {
        Assert.Equal("-2 ($FE)", LocalValueFormatter.Format([0xFE], Type(1, signed: true)));
        Assert.Equal("-1 ($FFFFFFFF)", LocalValueFormatter.Format([0xFF, 0xFF, 0xFF, 0xFF], Type(4, signed: true)));
        Assert.Equal("65536 ($00010000)", LocalValueFormatter.Format([0x00, 0x00, 0x01, 0x00], Type(4)));
    }

    [Fact]
    public void UnknownTypes_UseTheSizeAlone_AndOddSizesShowRawBytes()
    {
        Assert.Equal("4660 ($1234)", LocalValueFormatter.Format([0x34, 0x12], null));
        Assert.Equal("01 02 03", LocalValueFormatter.Format([1, 2, 3], null));
        Assert.Equal("?", LocalValueFormatter.Format([], null));
    }
}
