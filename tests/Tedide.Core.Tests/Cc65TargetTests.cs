namespace Tedide.Core.Tests;

public class Cc65TargetTests
{
    [Theory]
    [InlineData(Cc65Target.C64, "c64")]
    [InlineData(Cc65Target.C128, "c128")]
    [InlineData(Cc65Target.C16, "c16")]
    [InlineData(Cc65Target.Plus4, "plus4")]
    [InlineData(Cc65Target.Vic20, "vic20")]
    [InlineData(Cc65Target.Pet, "pet")]
    [InlineData(Cc65Target.Apple2, "apple2")]
    [InlineData(Cc65Target.Apple2Enh, "apple2enh")]
    [InlineData(Cc65Target.Atari, "atari")]
    [InlineData(Cc65Target.Atari5200, "atari5200")]
    [InlineData(Cc65Target.Nes, "nes")]
    [InlineData(Cc65Target.Atmos, "atmos")]
    [InlineData(Cc65Target.Cbm510, "cbm510")]
    [InlineData(Cc65Target.Cbm610, "cbm610")]
    [InlineData(Cc65Target.Geos_Cbm, "geos-cbm")]
    [InlineData(Cc65Target.Lynx, "lynx")]
    [InlineData(Cc65Target.None, "none")]
    public void ToCl65Id_MatchesCl65TargetSyntax(Cc65Target target, string expectedId)
    {
        Assert.Equal(expectedId, target.ToCl65Id());
    }

    [Theory]
    [InlineData(Cc65Target.C64, ".prg")]
    [InlineData(Cc65Target.C128, ".prg")]
    [InlineData(Cc65Target.C16, ".prg")]
    [InlineData(Cc65Target.Plus4, ".prg")]
    [InlineData(Cc65Target.Vic20, ".prg")]
    [InlineData(Cc65Target.Pet, ".prg")]
    [InlineData(Cc65Target.Cbm510, ".prg")]
    [InlineData(Cc65Target.Cbm610, ".prg")]
    [InlineData(Cc65Target.Apple2, ".bin")]
    [InlineData(Cc65Target.Apple2Enh, ".bin")]
    [InlineData(Cc65Target.Atari5200, ".bin")]
    [InlineData(Cc65Target.Atmos, ".bin")]
    [InlineData(Cc65Target.None, ".bin")]
    [InlineData(Cc65Target.Atari, ".xex")]
    [InlineData(Cc65Target.Nes, ".nes")]
    [InlineData(Cc65Target.Geos_Cbm, ".cvt")]
    [InlineData(Cc65Target.Lynx, ".lnx")]
    public void DefaultOutputExtension_MatchesConventionalExtension(Cc65Target target, string expectedExtension)
    {
        Assert.Equal(expectedExtension, target.DefaultOutputExtension());
    }

    [Theory]
    [InlineData(Cc65Target.C64)]
    [InlineData(Cc65Target.C128)]
    [InlineData(Cc65Target.C16)]
    [InlineData(Cc65Target.Plus4)]
    [InlineData(Cc65Target.Vic20)]
    [InlineData(Cc65Target.Pet)]
    [InlineData(Cc65Target.Apple2)]
    [InlineData(Cc65Target.Apple2Enh)]
    [InlineData(Cc65Target.Atari)]
    [InlineData(Cc65Target.Atari5200)]
    [InlineData(Cc65Target.Nes)]
    [InlineData(Cc65Target.Atmos)]
    [InlineData(Cc65Target.Cbm510)]
    [InlineData(Cc65Target.Cbm610)]
    [InlineData(Cc65Target.Geos_Cbm)]
    [InlineData(Cc65Target.Lynx)]
    [InlineData(Cc65Target.None)]
    public void TryParse_RoundTripsEveryCl65Id(Cc65Target target)
    {
        var parsed = Cc65TargetExtensions.TryParse(target.ToCl65Id(), out var result);

        Assert.True(parsed);
        Assert.Equal(target, result);
    }

    [Theory]
    [InlineData("C64")]
    [InlineData("C64 ")]
    [InlineData(" c64")]
    [InlineData("Apple2Enh")]
    public void TryParse_IsCaseInsensitiveAndTrimsWhitespace(string text)
    {
        var parsed = Cc65TargetExtensions.TryParse(text, out _);

        Assert.True(parsed);
    }

    [Fact]
    public void TryParse_RejectsUnknownText()
    {
        var parsed = Cc65TargetExtensions.TryParse("not a real target", out _);

        Assert.False(parsed);
    }

    [Fact]
    public void CommodoreTargets_ExcludesNonCommodoreHardware()
    {
        Assert.DoesNotContain(Cc65Target.Apple2, Cc65TargetExtensions.CommodoreTargets);
        Assert.DoesNotContain(Cc65Target.Apple2Enh, Cc65TargetExtensions.CommodoreTargets);
        Assert.DoesNotContain(Cc65Target.Atari, Cc65TargetExtensions.CommodoreTargets);
        Assert.DoesNotContain(Cc65Target.Atari5200, Cc65TargetExtensions.CommodoreTargets);
        Assert.DoesNotContain(Cc65Target.Nes, Cc65TargetExtensions.CommodoreTargets);
        Assert.DoesNotContain(Cc65Target.Atmos, Cc65TargetExtensions.CommodoreTargets);
        Assert.DoesNotContain(Cc65Target.Lynx, Cc65TargetExtensions.CommodoreTargets);
        Assert.DoesNotContain(Cc65Target.None, Cc65TargetExtensions.CommodoreTargets);
    }

    [Theory]
    [InlineData(Cc65Target.C64)]
    [InlineData(Cc65Target.C128)]
    [InlineData(Cc65Target.C16)]
    [InlineData(Cc65Target.Plus4)]
    [InlineData(Cc65Target.Vic20)]
    [InlineData(Cc65Target.Pet)]
    [InlineData(Cc65Target.Cbm510)]
    [InlineData(Cc65Target.Cbm610)]
    [InlineData(Cc65Target.Geos_Cbm)]
    public void CommodoreTargets_IncludesEveryCommodoreMachine(Cc65Target target)
    {
        Assert.Contains(target, Cc65TargetExtensions.CommodoreTargets);
    }
}
