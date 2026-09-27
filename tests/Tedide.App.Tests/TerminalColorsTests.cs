using Tedide.Theming;

namespace Tedide.App.Tests;

/// <summary>
/// Terminal.Gui forces 16-color mode inside Windows Terminal; TerminalColors undoes that only
/// where it's safe to.
/// </summary>
public class TerminalColorsTests
{
    [Theory]
    [InlineData(true, true, "a-session-guid", true)]    // Windows Terminal: back to 24-bit
    [InlineData(true, true, null, false)]               // not Windows Terminal: leave it alone
    [InlineData(true, true, "", false)]
    [InlineData(true, false, "a-session-guid", false)]  // driver can't do true color anyway
    [InlineData(false, true, "a-session-guid", false)]  // already true color
    public void ShouldUseTrueColor_OnlyInWindowsTerminal(bool force16, bool supportsTrueColor, string? wtSession, bool expected)
    {
        Assert.Equal(expected, TerminalColors.ShouldUseTrueColor(force16, supportsTrueColor, wtSession));
    }

    [Fact]
    public void UseTrueColorInWindowsTerminal_IgnoresANullDriver()
    {
        TerminalColors.UseTrueColorInWindowsTerminal(null);
    }
}
