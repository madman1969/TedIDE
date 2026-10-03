using Tedide.Core.Debugging;

namespace Tedide.Core.Tests.Debugging;

public class DisassemblerTests
{
    private static DisassembledInstruction Decode(params byte[] bytes) => Disassembler6502.Decode(bytes, 0, 0xC000);

    [Theory]
    [InlineData(new byte[] { 0xA9, 0x05 }, "lda #$05", "2")]
    [InlineData(new byte[] { 0xAD, 0x20, 0xD0 }, "lda $D020", "4")]
    [InlineData(new byte[] { 0xBD, 0x00, 0x04 }, "lda $0400,x", "4*")]
    [InlineData(new byte[] { 0xB1, 0xFB }, "lda ($FB),y", "5*")]
    [InlineData(new byte[] { 0xA1, 0xFB }, "lda ($FB,x)", "6")]
    [InlineData(new byte[] { 0x91, 0xFB }, "sta ($FB),y", "6")]
    [InlineData(new byte[] { 0x9D, 0x00, 0x04 }, "sta $0400,x", "5")]
    [InlineData(new byte[] { 0xB6, 0x10 }, "ldx $10,y", "4")]
    [InlineData(new byte[] { 0x0A }, "asl a", "2")]
    [InlineData(new byte[] { 0x1E, 0x00, 0x04 }, "asl $0400,x", "7")]
    [InlineData(new byte[] { 0x6C, 0xFC, 0xFF }, "jmp ($FFFC)", "5")]
    [InlineData(new byte[] { 0x20, 0xD2, 0xFF }, "jsr $FFD2", "6")]
    [InlineData(new byte[] { 0x60 }, "rts", "6")]
    [InlineData(new byte[] { 0xE8 }, "inx", "2")]
    public void Decode_KnowsTheNmosInstructionSet(byte[] bytes, string text, string cycles)
    {
        var instruction = Decode(bytes);

        Assert.True(instruction.IsKnown);
        Assert.Equal(text, instruction.Text);
        Assert.Equal(cycles, instruction.Cycles);
        Assert.Equal(bytes.Length, instruction.Length);
    }

    [Fact]
    public void Decode_ResolvesBranchTargetsBothWays()
    {
        Assert.Equal((ushort)0xC012, Decode(0xD0, 0x10).Target);
        Assert.Equal("bne $BFFE", Decode(0xD0, 0xFC).Text);
        Assert.Equal("2**", Decode(0xD0, 0xFC).Cycles);
    }

    [Fact]
    public void Decode_ShowsUnknownAndTruncatedOpcodesAsBytes()
    {
        Assert.Equal(".byte $02", Decode(0x02).Text);
        Assert.False(Decode(0x02).IsKnown);
        Assert.Equal(".byte $AD", Decode(0xAD, 0x20).Text);
        Assert.Equal("D0 20", Disassembler6502.Decode([0xAD, 0xD0, 0x20], 1, 0).BytesText);
    }

    [Fact]
    public void Decode_UsesThe65C02AdditionsOnlyWhenAsked()
    {
        Assert.False(Decode(0x64, 0x10).IsKnown);
        Assert.Equal("stz $10", Disassembler6502.Decode([0x64, 0x10], 0, 0, cmos: true).Text);
        Assert.Equal("lda ($FB)", Disassembler6502.Decode([0xB2, 0xFB], 0, 0, cmos: true).Text);
        Assert.Equal("bra $0012", Disassembler6502.Decode([0x80, 0x10], 0, 0, cmos: true).Text);
        Assert.Equal("inc a", Disassembler6502.Decode([0x1A], 0, 0, cmos: true).Text);
        Assert.Equal("jmp ($1234,x)", Disassembler6502.Decode([0x7C, 0x34, 0x12], 0, 0, cmos: true).Text);
        Assert.True(Disassembler6502.IsCmos("65816"));
        Assert.False(Disassembler6502.IsCmos("6502"));
    }

    [Fact]
    public void Disassemble_WalksConsecutiveInstructions()
    {
        byte[] code = [0xA9, 0x00, 0x8D, 0x20, 0xD0, 0x60];

        var instructions = Disassembler6502.Disassemble(code, 0x0840);

        Assert.Equal(["lda #$00", "sta $D020", "rts"], instructions.Select(i => i.Text));
        Assert.Equal([(ushort)0x0840, (ushort)0x0842, (ushort)0x0845], instructions.Select(i => i.Address));
        Assert.Single(Disassembler6502.Disassemble(code, 0x0840, startOffset: 2, maxCount: 1));
    }

    [Fact]
    public void FindStartBefore_LandsExactlyOnTheTarget()
    {
        // lda #$00 / sta $D020 / ldx #$08 / dex / bne -3 ; target = the dex at offset 7.
        byte[] code = [0xA9, 0x00, 0x8D, 0x20, 0xD0, 0xA2, 0x08, 0xCA, 0xD0, 0xFD];

        var start = Disassembler6502.FindStartBefore(code, 7, maxBack: 7);
        var instructions = Disassembler6502.Disassemble(code, 0, start);

        Assert.Equal(0, start);
        Assert.Contains(instructions, i => i.Address == 7 && i.Text == "dex");
        Assert.Equal(3, Disassembler6502.FindStartBefore(code, 3, maxBack: 0));
    }
}

public class CallStackWalkerTests
{
    /// <summary>A stack page with the given bytes just above <paramref name="sp"/>.</summary>
    private static byte[] Stack(byte sp, params byte[] pushed)
    {
        var page = new byte[256];
        pushed.CopyTo(page, sp + 1);
        return page;
    }

    [Fact]
    public void Walk_FindsReturnAddressesThatFollowAJsr_InnermostFirst()
    {
        // A JSR at $0850 pushes $0852; one at $0900 pushes $0902. A data byte sits between them.
        var page = Stack(0xF0, 0x52, 0x08, 0x77, 0x02, 0x09);
        var jsrs = new HashSet<ushort> { 0x0850, 0x0900 };

        var sites = CallStackWalker.Walk(page, 0xF0, jsrs.Contains);

        Assert.Equal([(ushort)0x0850, (ushort)0x0900], sites);
    }

    [Fact]
    public void Walk_ConsumesAMatchedPairWhole_AndRespectsTheFrameLimit()
    {
        var page = Stack(0xF8, 0x52, 0x08, 0x52, 0x08, 0x52, 0x08);

        Assert.Equal(3, CallStackWalker.Walk(page, 0xF8, a => a == 0x0850).Count);
        Assert.Equal(2, CallStackWalker.Walk(page, 0xF8, a => a == 0x0850, maxFrames: 2).Count);
        Assert.Empty(CallStackWalker.Walk(page, 0xFF, _ => true));
    }

    [Fact]
    public void CandidateCallSites_ListsEveryPairAboveTheStackPointer()
    {
        var page = Stack(0xFC, 0x52, 0x08, 0x10);

        var candidates = CallStackWalker.CandidateCallSites(page, 0xFC).ToList();

        Assert.Equal(2, candidates.Count);
        Assert.Equal((ushort)0x0850, candidates[0]);
        Assert.Equal((ushort)0x1006, candidates[1]);
    }
}

public class MemoryDumpTests
{
    [Fact]
    public void Rows_SplitIntoSixteenBytesWithText()
    {
        var bytes = Enumerable.Range(0x40, 20).Select(b => (byte)b).ToArray();

        var rows = MemoryDump.Rows(0xC000, bytes);

        Assert.Equal(2, rows.Count);
        Assert.Equal((ushort)0xC010, rows[1].Address);
        Assert.Equal("40", rows[0].Hex[0]);
        Assert.Equal("@ABCDEFGHIJKLMNO", rows[0].Text);
        Assert.Equal("  ", rows[1].Hex[15]);
    }

    [Theory]
    [InlineData(0x41, 'A')]
    [InlineData(0xC1, 'A')]
    [InlineData(0x0D, '.')]
    [InlineData(0xFF, '.')]
    public void Printable_ShowsAsciiAndPetsciiCapitals(byte value, char expected)
    {
        Assert.Equal(expected, MemoryDump.Printable(value));
    }

    [Fact]
    public void ChangedOffsets_FindsTheDifferences()
    {
        Assert.Equal([1, 3], MemoryDump.ChangedOffsets([1, 2, 3, 4], [1, 9, 3, 0]).Order());
        Assert.Empty(MemoryDump.ChangedOffsets([1], [1, 2]));
    }
}

public class DbgFileLabelTests
{
    private static DbgFile Load() =>
        DbgFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "HelloCBM.dbg")));

    [Fact]
    public void FindLabelAt_AndFindNearestLabel_UseRealNamesNotCompilerLocals()
    {
        var dbg = Load();

        // _main is at $0840, and so is cc65's own L-label for it in some builds - the real name wins.
        Assert.Equal("_main", dbg.FindLabelAt(0x0840));
        Assert.Equal("_main+3", dbg.FindNearestLabel(0x0843));
        Assert.Null(dbg.FindLabelAt(0x0843));
        Assert.Null(dbg.FindNearestLabel(0x0001));
    }

    [Fact]
    public void IsInReadOnlySegment_CoversCodeButNotData()
    {
        var dbg = Load();

        Assert.True(dbg.IsInReadOnlySegment(0x0840));   // CODE
        Assert.False(dbg.IsInReadOnlySegment(0x0BD0));  // DATA (rw)
        Assert.False(dbg.IsInReadOnlySegment(0xC000));  // nowhere
    }
}
