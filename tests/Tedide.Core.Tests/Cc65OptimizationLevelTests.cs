namespace Tedide.Core.Tests;

public class Cc65OptimizationLevelTests
{
    [Fact]
    public void None_HasNoCl65Flag()
    {
        Assert.Null(Cc65OptimizationLevel.None.ToCl65Flag());
    }

    [Theory]
    [InlineData(Cc65OptimizationLevel.Standard, "-O")]
    [InlineData(Cc65OptimizationLevel.Inline, "-Oi")]
    [InlineData(Cc65OptimizationLevel.Register, "-Or")]
    [InlineData(Cc65OptimizationLevel.InlineKnownFunctions, "-Os")]
    [InlineData(Cc65OptimizationLevel.Extended, "-Ox")]
    [InlineData(Cc65OptimizationLevel.Maximum, "-Oirs")]
    public void ToCl65Flag_MatchesCl65OptionSyntax(Cc65OptimizationLevel level, string expectedFlag)
    {
        Assert.Equal(expectedFlag, level.ToCl65Flag());
    }

    [Theory]
    [InlineData(Cc65OptimizationLevel.None)]
    [InlineData(Cc65OptimizationLevel.Standard)]
    [InlineData(Cc65OptimizationLevel.Inline)]
    [InlineData(Cc65OptimizationLevel.Register)]
    [InlineData(Cc65OptimizationLevel.InlineKnownFunctions)]
    [InlineData(Cc65OptimizationLevel.Extended)]
    [InlineData(Cc65OptimizationLevel.Maximum)]
    public void TryParse_RoundTripsEveryDisplayName(Cc65OptimizationLevel level)
    {
        var parsed = Cc65OptimizationLevelExtensions.TryParse(level.DisplayName(), out var result);

        Assert.True(parsed);
        Assert.Equal(level, result);
    }

    [Fact]
    public void TryParse_RejectsUnknownText()
    {
        var parsed = Cc65OptimizationLevelExtensions.TryParse("not a real level", out _);

        Assert.False(parsed);
    }
}
