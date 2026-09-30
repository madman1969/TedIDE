namespace Tedide.Build.Tests;

public class Opt6502StatsTests
{
    private const string Line =
        "opt6502-stats: optimizations=6 removed=5 rewritten=1 bytes=17 cycles=19 reload=0 constant=0 transfer=0 jump=5 unreachable=0 stz=1";

    [Fact]
    public void TryParse_ReadsEveryField()
    {
        Assert.True(Opt6502Stats.TryParse(Line, out var stats));

        Assert.Equal(6, stats.Optimizations);
        Assert.Equal(5, stats.Removed);
        Assert.Equal(1, stats.Rewritten);
        Assert.Equal(17, stats.Bytes);
        Assert.Equal(19, stats.Cycles);
        Assert.Equal(5, stats.ByKind["jump"]);
        Assert.Equal(1, stats.ByKind["stz"]);
        Assert.False(stats.ByKind.ContainsKey("bytes"));
    }

    [Theory]
    [InlineData("Error: Cannot open foo.s")]
    [InlineData("")]
    [InlineData(" opt6502-stats: optimizations=1")]
    public void TryParse_RejectsAnyOtherLine(string line)
    {
        Assert.False(Opt6502Stats.TryParse(line, out _));
    }

    [Fact]
    public void TryParse_IgnoresMalformedFields_AndKeepsUnknownKinds()
    {
        Assert.True(Opt6502Stats.TryParse("opt6502-stats: optimizations=2 bytes=oops future=2 junk", out var stats));

        Assert.Equal(2, stats.Optimizations);
        Assert.Equal(0, stats.Bytes);
        Assert.Equal(2, stats.ByKind["future"]);
    }

    [Fact]
    public void Add_SumsEveryField()
    {
        Opt6502Stats.TryParse(Line, out var a);
        Opt6502Stats.TryParse("opt6502-stats: optimizations=1 removed=1 bytes=2 cycles=2 constant=1", out var b);

        var sum = a.Add(b);

        Assert.Equal(7, sum.Optimizations);
        Assert.Equal(6, sum.Removed);
        Assert.Equal(19, sum.Bytes);
        Assert.Equal(21, sum.Cycles);
        Assert.Equal(1, sum.ByKind["constant"]);
        Assert.Equal(5, sum.ByKind["jump"]);
    }

    [Fact]
    public void Describe_ListsOnlyTheKindsThatFired_InOpt6502sOrder()
    {
        Opt6502Stats.TryParse(Line, out var stats);

        Assert.Equal("6 optimizations (5 jump to next line, 1 STZ rewrite), ~17 bytes and ~19 cycles saved", stats.Describe());
    }

    [Fact]
    public void Describe_UsesTheSingular_ForOneOptimization()
    {
        Opt6502Stats.TryParse("opt6502-stats: optimizations=1 removed=1 bytes=3 cycles=3 jump=1 later=0", out var stats);

        Assert.Equal("1 optimization (1 jump to next line), ~3 bytes and ~3 cycles saved", stats.Describe());
    }

    [Fact]
    public void Describe_NamesAnUnknownKind_ByItsKey()
    {
        Opt6502Stats.TryParse("opt6502-stats: optimizations=2 bytes=1 cycles=1 future=2", out var stats);

        Assert.Equal("2 optimizations (2 future), ~1 bytes and ~1 cycles saved", stats.Describe());
    }

    [Fact]
    public void Describe_SaysBytesAdded_WhenInliningGrewTheCode()
    {
        Opt6502Stats.TryParse("opt6502-stats: optimizations=11 removed=11 rewritten=1 bytes=-190 cycles=126 inline=11", out var stats);

        Assert.Equal("11 optimizations (11 runtime call inlined), ~190 bytes added, ~126 cycles saved", stats.Describe());
    }

    [Fact]
    public void Describe_NamesThreadedJumps()
    {
        Opt6502Stats.TryParse("opt6502-stats: optimizations=5 removed=1 rewritten=4 bytes=5 cycles=15 jump=1 thread=4", out var stats);

        Assert.Equal("5 optimizations (1 jump to next line, 4 jump threaded), ~5 bytes and ~15 cycles saved", stats.Describe());
    }

    [Fact]
    public void Describe_SaysSo_WhenNothingWasFound()
    {
        Assert.Equal("no optimizations found", Opt6502Stats.Empty.Describe());
    }
}
