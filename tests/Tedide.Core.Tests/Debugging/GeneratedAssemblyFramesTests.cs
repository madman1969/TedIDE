using Tedide.Core.Debugging;

namespace Tedide.Core.Tests.Debugging;

/// <summary>
/// Frame depths from cl65's real generated assembly for samples/CBMInfo's video.c. The depths at
/// lines 35 and 39 of detect_video_system - and the addresses they give - were confirmed live
/// against VICE: loop counter "i" read back 0,1,2... at line 35 and 6,7,8... at line 39, where
/// the loop body's own block locals sit 4 bytes further down the stack.
/// </summary>
public class GeneratedAssemblyFramesTests
{
    private static readonly string[] FixtureLines =
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CBMInfo.video.c.s"));

    private static IReadOnlyList<FunctionFrame> Frames { get; } =
        GeneratedAssemblyFrames.Parse(string.Join("\n", FixtureLines));

    private static FunctionFrame Frame(string name) => Frames.Single(f => f.Name == name);

    /// <summary>The depth before the first instruction of the <paramref name="occurrence"/>th
    /// block of code for video.c line <paramref name="cLine"/>.</summary>
    private static int DepthAtCLine(FunctionFrame frame, int cLine, int occurrence = 1)
    {
        var marker = Enumerable.Range(frame.FirstLine, frame.LastLine - frame.FirstLine)
            .Where(l => FixtureLines[l - 1].Contains($"\"src/video.c\", {cLine}", StringComparison.Ordinal)
                && FixtureLines[l - 1].Contains(".dbg", StringComparison.Ordinal))
            .ElementAt(occurrence - 1);
        var instruction = Enumerable.Range(marker + 1, frame.LastLine - marker).First(frame.DepthBeforeLine.ContainsKey);
        return frame.DepthBeforeLine[instruction];
    }

    [Fact]
    public void Parse_ReadsEachFunctionsNameAndStackSymbols()
    {
        var detect = Frame("detect_video_system");

        Assert.Equal([new FrameSymbol("max_raster", -2), new FrameSymbol("i", -4)], detect.Symbols);
        Assert.Equal(["cols", "rows", "c", "r"], Frame("video_get_text_size").Symbols.Select(s => s.Name));
        Assert.All(Frames, f => Assert.True(f.Reliable, $"{f.Name} not followable"));
    }

    [Theory]
    [InlineData(30, 1, 0)] // unsigned max_raster = 0;       - nothing allocated yet
    [InlineData(33, 1, 2)] // for (i = 0; ...                 - max_raster pushed
    [InlineData(35, 1, 4)] // unsigned char lo = ...;         - i allocated (decsp2)       [live: i at sp+0]
    [InlineData(36, 1, 5)] // unsigned char hi8 = ...;        - lo pushed
    [InlineData(37, 1, 6)] // unsigned current = ...;         - hi8 pushed
    [InlineData(39, 1, 8)] // if (current > max_raster)       - current pushed             [live: i at sp+4]
    [InlineData(43, 1, 8)] // } (jsr incsp4 releases the block locals)
    [InlineData(33, 2, 4)] // ++i, after the block locals are gone
    [InlineData(47, 1, 4)] // return ... (reached by branch, after a jmp)
    public void DetectVideoSystem_DepthsFollowEveryPushAndPop(int cLine, int occurrence, int expectedDepth)
    {
        Assert.Equal(expectedDepth, DepthAtCLine(Frame("detect_video_system"), cLine, occurrence));
    }

    [Theory]
    [InlineData(102, -2)] // { - the last parameter is still in A/X until "jsr pushax"
    [InlineData(105, 0)]  // screensize(&c, &r); - frame starts here, c/r allocated by decsp2
    [InlineData(106, 2)]  // *cols = c; - screensize popped its own argument
    [InlineData(107, 2)]
    public void VideoGetTextSize_StartsItsFrameAfterThePushedFastcallParameter(int cLine, int expectedDepth)
    {
        Assert.Equal(expectedDepth, DepthAtCLine(Frame("video_get_text_size"), cLine));
    }

    [Fact]
    public void SlotsAt_GivesTheAddressesConfirmedLiveInVICE()
    {
        var frame = Frame("detect_video_system");

        var slots = frame.SlotsAt(depth: 8, sp: 0xCFF0, types: new Dictionary<string, CVariableType>());

        Assert.Equal((ushort)0xCFF6, slots.Single(s => s.Symbol.Name == "max_raster").Address);
        Assert.Equal((ushort)0xCFF4, slots.Single(s => s.Symbol.Name == "i").Address);
        Assert.All(slots, s => Assert.Equal(2, s.Size));
    }

    [Fact]
    public void SlotsAt_HasNoAddressForSymbolsNotYetOnTheStack()
    {
        var slots = Frame("detect_video_system").SlotsAt(depth: 2, sp: 0xCFF6, types: new Dictionary<string, CVariableType>());

        Assert.Equal((ushort)0xCFF6, slots.Single(s => s.Symbol.Name == "max_raster").Address);
        Assert.Null(slots.Single(s => s.Symbol.Name == "i").Address);
    }

    [Fact]
    public void SlotsAt_ListsParametersThenLocals_InDeclarationOrder()
    {
        var slots = Frame("video_get_text_size").SlotsAt(0, 0x1000, new Dictionary<string, CVariableType>());

        Assert.Equal(["cols", "rows", "c", "r"], slots.Select(s => s.Symbol.Name));
    }

    [Fact]
    public void SizeFromLayout_ComesFromTheGapsBetweenOffsets()
    {
        var frame = Frame("video_get_text_size");
        int? Size(string name) => frame.SizeFromLayout(frame.Symbols.Single(s => s.Name == name));

        Assert.Equal(1, Size("c"));
        Assert.Equal(1, Size("r"));
        Assert.Equal(2, Size("rows"));
        Assert.Null(Size("cols")); // the highest parameter: nothing above it to measure against
    }

    [Fact]
    public void AssignmentsThroughAPointerAroundACall_DontDriftTheFrame()
    {
        // "info->model = machine_model();" pushes info (pushw0sp), calls, then pops it storing
        // the result (staxspidx). Counting that push as the call's argument once let the pop eat
        // into the frame: info read back from A/X halfway through the function, depth -8 by line
        // 17. Seen live in samples/CBMInfo.
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CBMInfo.main.c.s"));
        var main = GeneratedAssemblyFrames.Parse(string.Join("\n", lines)).Single(f => f.Name == "detect_system");

        // The depth at the first instruction of every statement - where a debugger stops.
        var statementStarts = Enumerable.Range(main.FirstLine, main.LastLine - main.FirstLine)
            .Where(l => lines[l - 1].Contains(".dbg\tline, \"src/", StringComparison.Ordinal))
            .Select(marker => Enumerable.Range(marker + 1, main.LastLine - marker).First(main.DepthBeforeLine.ContainsKey))
            .Select(instruction => main.DepthBeforeLine[instruction])
            .ToList();

        Assert.Equal(-2, statementStarts[0]); // "{": before "jsr pushax" pushes info
        Assert.All(statementStarts.Skip(1), depth => Assert.Equal(0, depth));
        Assert.True(statementStarts.Count > 15);
    }

    [Theory]
    [InlineData("CBMInfo.video.c.s")]
    [InlineData("CBMInfo.main.c.s")]
    public void NoFunctionsFrame_EverDropsBelowWhereItStarted(string fixture)
    {
        foreach (var frame in GeneratedAssemblyFrames.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture))))
        {
            if (frame.DepthBeforeLine.Count == 0)
                continue;
            var start = frame.DepthBeforeLine[frame.DepthBeforeLine.Keys.Min()];
            Assert.True(frame.DepthBeforeLine.Values.Min() >= start, $"{frame.Name} drifts below its start depth {start}");
        }
    }

    [Fact]
    public void AFunctionUsingAnUnknownStackHelper_IsMarkedUnreliable()
    {
        const string asm = """
            .proc	_f: near
            	.dbg	func, "f", "00", static, "_f"
            	.dbg	sym, "x", "00", auto, -2
            	.dbg	line, "src/f.c", 3
            	jsr     somethingnew
            	rts
            .endproc
            """;

        Assert.False(GeneratedAssemblyFrames.Parse(asm).Single().Reliable);
    }

    [Fact]
    public void DepthAt_UsesTheNearestEarlierInstruction()
    {
        var frame = Frame("detect_video_system");
        var firstInstruction = frame.DepthBeforeLine.Keys.Min();

        Assert.Equal(frame.DepthBeforeLine[firstInstruction], frame.DepthAt(firstInstruction));
        Assert.Null(frame.DepthAt(frame.FirstLine));
    }
}
