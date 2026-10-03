using Tedide.Build.Opt6502;
using Tedide.Core;

namespace Tedide.Build.Tests;

/// <summary>
/// Opt6502Optimizer's rules one at a time - each test is a small ca65 fragment where an
/// optimization must, or must not, happen. Several pin bugs the original opt6502 had (see the
/// History section of src/Tedide.Build/Opt6502/README.md): those are named for what goes wrong if
/// the rule regresses.
/// </summary>
public class Opt6502OptimizerTests
{
    /// <summary>The header RuntimeInliner requires before it will inline anything.</summary>
    private const string Cc65Header =
        "\t.fopt\t\tcompiler,\"cc65 v 2.19 - Git b75f872\"\n" +
        "\t.setcpu\t\t\"6502\"\n" +
        "\t.importzp\tsp, sreg, regsave, regbank\n" +
        "\t.importzp\ttmp1, tmp2, tmp3, tmp4, ptr1, ptr2, ptr3, ptr4\n" +
        "\t.macpack\tlongbranch\n";

    private static Opt6502Result Optimize(string source, Opt6502Mode mode = Opt6502Mode.Size, string cpu = "6502", int? inlineLimit = null) =>
        Opt6502Optimizer.Optimize(source, new Opt6502Options(mode, cpu, inlineLimit), newline: "\n");

    private static string[] Lines(Opt6502Result result) => result.Output.TrimEnd('\n').Split('\n');

    // --- parsing and output -------------------------------------------------------------------

    [Fact]
    public void UnchangedSource_IsWrittenBackExactly_IncludingCommentsBlankLinesAndDirectives()
    {
        const string source =
            "; a comment\n\n.segment\t\"CODE\"\n.proc\t_f: near\n\tlda     _x\t\t; load\n\t.byte\t\"a;b\", $00\nfoo = 5\nL0001:\n.endproc\n";

        var result = Optimize(source);

        Assert.Equal(source, result.Output);
        Assert.Equal(0, result.Stats.Optimizations);
    }

    [Fact]
    public void ColumnZeroDirectives_AreNotLabels()
    {
        // The original opt6502 wrote ".segment" back as ".segment:" - a label.
        var line = AsmLine.Parse(".segment\t\"CODE\"");

        Assert.Null(line.Label);
        Assert.Equal(".segment", line.Opcode);
        Assert.Equal("\"CODE\"", line.Operand);
    }

    [Theory]
    [InlineData("L0001:\tlda #1", "L0001", "lda", "#1")]
    [InlineData("foo::", "foo:", null, null)]
    [InlineData("x:= 5", null, "x:=", "5")]
    [InlineData("\tlda #';'\t; quoted semicolon", null, "lda", "#';'")]
    [InlineData("", null, null, null)]
    public void Parse_FollowsCa65LabelAndCommentRules(string source, string? label, string? opcode, string? operand)
    {
        var line = AsmLine.Parse(source);

        Assert.Equal(label, line.Label);
        Assert.Equal(opcode, line.Opcode);
        Assert.Equal(operand, line.Operand);
    }

    [Fact]
    public void Optimize_ReadsCrLfAndLfAlike()
    {
        var lf = Optimize(".proc\t_f: near\n\tjmp     L0001\nL0001:\trts\n.endproc\n");
        var crlf = Optimize(".proc\t_f: near\r\n\tjmp     L0001\r\nL0001:\trts\r\n.endproc\r\n");

        Assert.Equal(lf.Output, crlf.Output);
        Assert.Equal(1, crlf.Stats.ByKind["jump"]);
    }

    [Fact]
    public void Optimize_RejectsAnUnknownCpu()
    {
        Assert.Throws<ArgumentException>(() => Optimize("\tnop\n", cpu: "z80"));
    }

    // --- size passes ----------------------------------------------------------------------------

    [Fact]
    public void RedundantReload_IsRemoved_ButNotFromALiteralAddress()
    {
        var result = Optimize("\tlda     _x\n\tsta     _y\n\tlda     _x\n\tlda     $D012\n\tsta     _y\n\tlda     $D012\n");

        Assert.Equal(["\tlda     _x", "\tsta     _y", "\tlda     $D012", "\tsta     _y", "\tlda     $D012"], Lines(result));
        Assert.Equal(1, result.Stats.ByKind["reload"]);
    }

    [Fact]
    public void RepeatedConstantLoad_IsRemoved_AcrossStores_ButNotAcrossAJsr()
    {
        // The original opt6502 kept assuming A across JSR, which clobbers it.
        var result = Optimize("\tlda     #$00\n\tsta     _a\n\tlda     #$00\n\tsta     _b\n\tjsr     pusha\n\tlda     #$00\n");

        Assert.Equal(["\tlda     #$00", "\tsta     _a", "\tsta     _b", "\tjsr     pusha", "\tlda     #$00"], Lines(result));
    }

    [Fact]
    public void RepeatedConstantLoad_IsKept_WhenTheFlagsChangedInBetween()
    {
        var result = Optimize("\tldx     #$00\n\tlda     _a\n\tldx     #$00\n\tbeq     L0001\nL0001:\trts\n");

        Assert.Equal(0, result.Stats.ByKind["constant"]);
    }

    [Fact]
    public void TaxTxa_RemovesOnlyTheTxa()
    {
        // The original opt6502 removed both, losing the value TAX put in X.
        var result = Optimize("\ttax\n\ttxa\n\tstx     _x\n");

        Assert.Equal(["\ttax", "\tstx     _x"], Lines(result));
    }

    [Fact]
    public void UnreachableInstructions_AfterRts_AreRemoved_ButDirectivesStay()
    {
        var result = Optimize("\trts\n\tldx     #$02\n\t.dbg\tline\n\tinx\n.endproc\n");

        Assert.Equal(["\trts", "\t.dbg\tline", ".endproc"], Lines(result));
        Assert.Equal(2, result.Stats.ByKind["unreachable"]);
    }

    [Fact]
    public void JumpToTheNextLabel_IsRemoved_ButAJumpOverOtherCodeIsNot()
    {
        // The original opt6502 deleted a JMP before *any* label - e.g. a loop's back-jump.
        var result = Optimize(".proc\t_f: near\n\tjmp     L0002\nL0001:\tinx\nL0002:\tiny\n\tjmp     L0003\n\t.dbg\tline\nL0003:\trts\n.endproc\n");

        Assert.Contains("\tjmp     L0002", Lines(result));
        Assert.DoesNotContain("\tjmp     L0003", Lines(result));
        Assert.Equal(1, result.Stats.ByKind["jump"]);
    }

    [Fact]
    public void JumpThreading_FollowsChains_AndTurnsAJumpToRtsIntoRts()
    {
        var result = Optimize(".proc\t_f: near\n\tjmp     L0001\n\tinx\nL0001:\tjmp     L0002\n\tiny\nL0002:\tjmp     incsp2\nL0003:\tjmp     L0004\n\tdex\nL0004:\trts\n.endproc\n");

        var lines = Lines(result);
        Assert.Contains("L0001:\tjmp     incsp2", lines);
        Assert.Contains("L0003:\trts", lines);
        Assert.True(result.Stats.ByKind["thread"] >= 2);
    }

    [Fact]
    public void JumpThreading_LeavesACycleAlone_AndStaysInsideItsOwnProc()
    {
        var result = Optimize(
            ".proc\t_a: near\nL0001:\tjmp     L0002\nL0002:\tjmp     L0001\n.endproc\n" +
            ".proc\t_b: near\n\tjmp     L0002\n\tinx\nL0002:\trts\n.endproc\n");

        var lines = Lines(result);
        Assert.Contains("L0001:\tjmp     L0002", lines);
        Assert.Contains("L0002:\tjmp     L0001", lines);
        Assert.Equal(0, result.Stats.ByKind["thread"]);
    }

    [Fact]
    public void NoOptRegion_IsLeftAlone()
    {
        var result = Optimize(";#NOOPT\n\ttax\n\ttxa\n;#OPT\n\ttay\n\ttya\n");

        Assert.Equal([";#NOOPT", "\ttax", "\ttxa", ";#OPT", "\ttay"], Lines(result));
    }

    // --- 65C02 / SuperCPU -------------------------------------------------------------------------

    [Fact]
    public void StoreZero_OnlyOnACmosCpu_OnlyInLegalModes_AndRaisesSetcpu()
    {
        const string source = "\t.setcpu\t\t\"6502\"\n\tlda     #$00\n\tsta     _a\n\tsta     _b,x\n\tlda     #$01\n";

        var nmos = Optimize(source, cpu: "6502");
        var cmos = Optimize(source, cpu: "65816");

        Assert.Equal(0, nmos.Stats.ByKind["stz"]);
        Assert.Equal(["\t.setcpu\t\t\"65C02\"", "\tstz     _a", "\tstz     _b,x", "\tlda     #$01"], Lines(cmos));
    }

    [Fact]
    public void StoreZero_IsNotUsed_ForAnIndirectStore_OrWhenTheZeroIsStillNeeded()
    {
        // The original opt6502 emitted STZ (zp),y - which doesn't exist - and dropped LDA #0
        // before a JSR that was still to use it.
        var indirect = Optimize("\tlda     #$00\n\tsta     (sp),y\n\tlda     #$01\n", cpu: "65c02");
        var handedOn = Optimize("\tlda     #$00\n\tsta     _a\n\tjsr     _g\n", cpu: "65c02");

        Assert.Equal(0, indirect.Stats.ByKind["stz"]);
        Assert.Equal(0, handedOn.Stats.ByKind["stz"]);
    }

    // --- speed mode: runtime inlining ---------------------------------------------------------------

    private const string LoopCallingPushax =
        ".segment\t\"CODE\"\n.proc\t_f: near\n\tjsr     pushax\nL0002:\tjsr     pushax\n\tdex\n\tbne     L0002\n\trts\n.endproc\n";

    [Fact]
    public void Inlining_ReplacesHelperCallsInsideLoops_Only()
    {
        var result = Optimize(Cc65Header + LoopCallingPushax, Opt6502Mode.Speed);

        var lines = Lines(result);
        Assert.Equal(1, result.Stats.ByKind["inline"]);
        Assert.Contains("\tjsr     pushax", lines);  // the call outside the loop stays
        Assert.Contains("L0002:", lines);             // the loop's label keeps its own line
        Assert.Contains("\tpha", lines);
        Assert.True(result.Stats.Bytes < 0);          // speed costs size
    }

    [Fact]
    public void Inlining_NeverHappensInSizeMode_OrWithoutTheCc65Header()
    {
        var size = Optimize(Cc65Header + LoopCallingPushax, Opt6502Mode.Size);
        var noHeader = Optimize("\t.importzp\tsp\n\t.macpack\tlongbranch\n" + LoopCallingPushax, Opt6502Mode.Speed);

        Assert.Equal(0, size.Stats.ByKind["inline"]);
        Assert.Equal(0, noHeader.Stats.ByKind["inline"]);
    }

    [Fact]
    public void Inlining_SkipsAHelper_WhoseZeroPageSymbolsTheFileDoesNotImport()
    {
        // mulax9 needs ptr1; this file imports only sp.
        const string source = "\t.fopt\t\tcompiler,\"cc65 v 2.19 - Git b75f872\"\n\t.importzp\tsp\n\t.macpack\tlongbranch\n" +
                              ".proc\t_f: near\nL0002:\tjsr     mulax9\n\tbne     L0002\n.endproc\n";

        Assert.Equal(0, Optimize(source, Opt6502Mode.Speed).Stats.ByKind["inline"]);
    }

    [Fact]
    public void Inlining_KeepsALabelledCallsLabel_SoTheBodyIsNotDeletedAsUnreachable()
    {
        // The first inlining left the label on the dead JSR; after `jmp L0003` the unreachable-code
        // pass then deleted the start of the inlined body.
        const string source = ".proc\t_f: near\nL0002:\tdex\n\tjne     L0005\n\tjmp     L0003\nL0005:\tjsr     decsp5\n\tldy     #$01\n\tjmp     L0002\nL0003:\trts\n.endproc\n";

        var lines = Lines(Optimize(Cc65Header + source, Opt6502Mode.Speed));

        var label = Array.IndexOf(lines, "L0005:");
        Assert.True(label >= 0);
        Assert.Equal(["\tlda sp", "\tsec", "\tsbc #5", "\tsta sp"], lines[(label + 1)..(label + 5)]);
    }

    [Fact]
    public void Inlining_TurnsAShortBranchItPushedOutOfRange_IntoALongBranch()
    {
        var body = string.Concat(Enumerable.Repeat("\tjsr     pushax\n", 9));
        var source = ".proc\t_f: near\nL0002:\tdex\n" + body + "\tbne     L0002\nL0003:\tjsr     incsp2\n\tdey\n\tbne     L0003\n\trts\n.endproc\n";

        var lines = Lines(Optimize(Cc65Header + source, Opt6502Mode.Speed));

        Assert.Contains("\tjne     L0002", lines);    // 9 x pushax is well past 127 bytes
        Assert.Contains("\tbne     L0003", lines);    // still close - unchanged
    }

    [Fact]
    public void InlineLimit_StopsAfterThatManyCallSites()
    {
        var body = string.Concat(Enumerable.Repeat("\tjsr     pushax\n", 3));
        var source = ".proc\t_f: near\nL0002:\tdex\n" + body + "\tjne     L0002\n.endproc\n";

        Assert.Equal(2, Optimize(Cc65Header + source, Opt6502Mode.Speed, inlineLimit: 2).Stats.ByKind["inline"]);
    }

    [Fact]
    public void EveryHelperBody_ParsesIntoInstructions_AndOnlyUsesSymbolsItDeclares()
    {
        foreach (var helper in RuntimeHelpers.ByName.Values)
        {
            foreach (var statement in helper.Body)
            {
                var line = AsmLine.Parse(statement.StartsWith('{') ? statement.Replace("{", "L").Replace("}", "") : "\t" + statement);
                Assert.False(line.Opcode is { } op && op.StartsWith('.'), $"{helper.Name}: directive in body");
            }
            var text = string.Join(' ', helper.Body);
            foreach (var zp in new[] { "sp", "tmp1", "ptr1", "sreg" })
            {
                var uses = System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{zp}\b");
                if (uses)
                    Assert.Contains(zp, helper.ZeroPage);
            }
        }
    }

    // --- stats --------------------------------------------------------------------------------------

    [Fact]
    public void StatsLine_RoundTripsThroughTryParse()
    {
        var stats = Optimize(".proc\t_f: near\n\tjmp     L0001\nL0001:\trts\n.endproc\n").Stats;

        Assert.True(Opt6502Stats.TryParse(stats.ToStatsLine(), out var parsed));
        Assert.Equal(stats.Optimizations, parsed.Optimizations);
        Assert.Equal(stats.ByKind["jump"], parsed.ByKind["jump"]);
        Assert.Equal(
            "opt6502-stats: optimizations=1 removed=1 rewritten=0 bytes=3 cycles=3 reload=0 constant=0 transfer=0 jump=1 unreachable=0 stz=0 inline=0 thread=0",
            stats.ToStatsLine());
    }
}
