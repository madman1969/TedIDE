using Tedide.Core.Navigation;

namespace Tedide.Core.Tests.Navigation;

public class PreprocessorConditionsTests
{
    private static readonly HashSet<string> C64 = ["__CC65__", "__CBM__", "__C64__"];

    private static bool? Evaluate(string condition) =>
        PreprocessorConditions.Evaluate(SourceTokenizer.TokenizeC(condition), C64);

    private static List<SourceScope> Inactive(string text) =>
        PreprocessorConditions.InactiveRanges(SourceTokenizer.TokenizeC(text), C64);

    [Theory]
    [InlineData("defined(__C64__)", true)]
    [InlineData("defined __C64__", true)]
    [InlineData("defined(__VIC20__)", false)]
    [InlineData("!defined(__VIC20__)", true)]
    [InlineData("defined(__C64__) && !defined(_C64_H)", true)]
    [InlineData("defined(__VIC20__) || defined(__C128__)", false)]
    [InlineData("(defined(__VIC20__) || defined(__C64__)) && 1", true)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void Evaluate_WorksOutDefinedLogic(string condition, bool expected)
    {
        Assert.Equal(expected, Evaluate(condition));
    }

    [Theory]
    [InlineData("__CC65__ >= 0x0219")]
    [InlineData("SOME_FLAG")]
    [InlineData("defined(")]
    [InlineData("2")]
    [InlineData("SOME_FLAG && defined(__C64__)")]
    public void Evaluate_IsUnsureAboutAnythingElse(string condition)
    {
        Assert.Null(Evaluate(condition));
    }

    [Fact]
    public void Evaluate_ShortCircuitsAroundUnknowns()
    {
        Assert.Equal(true, Evaluate("SOME_FLAG || defined(__C64__)"));
        Assert.Equal(false, Evaluate("SOME_FLAG && defined(__PET__)"));
    }

    [Fact]
    public void InactiveRanges_FollowsAnIfElifElseChain()
    {
        var ranges = Inactive("""
            #if defined(__VIC20__)
            vic
            #elif defined(__C64__)
            c64
            #elif defined(__C128__)
            c128
            #else
            other
            #endif
            after
            """);

        // c128 and other run together: the #else between them is in neither active branch.
        Assert.Equal([new SourceScope(2, 2), new SourceScope(6, 8)], ranges);
    }

    [Fact]
    public void InactiveRanges_HandlesIfdefIfndefAndNesting()
    {
        var ranges = Inactive("""
            #ifndef GUARD
            #ifdef __PET__
            pet
            #if defined(__C64__)
            nested
            #endif
            #endif
            #endif
            """);

        Assert.Equal([new SourceScope(3, 6)], ranges);
    }

    [Fact]
    public void InactiveRanges_KeepsBothBranchesOfAnUnknownCondition()
    {
        Assert.Empty(Inactive("#if VERSION > 2\na\n#else\nb\n#endif\n"));
    }

    [Fact]
    public void InactiveRanges_RunsToTheEndOfAFileWithAMissingEndif()
    {
        Assert.Equal([new SourceScope(2, 3)], Inactive("#if 0\na\nb\n"));
    }
}
