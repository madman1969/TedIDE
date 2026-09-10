namespace Tedide.Core.Tests;

public class LinkerMapFileTests
{
    // Real ld65 linker map output, captured by building samples/HelloCBM with the Linker tab's
    // "Generate linker map file" toggle on - see Tedide.Core.csproj's Fixtures item group and
    // LinkerMapFile's own doc comment for why a real file is used rather than a hand-written one.
    private static string LoadFixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "HelloCBM.lnk.map"));

    [Fact]
    public void Parse_FindsEveryModule_IncludingLibraryMembers()
    {
        var map = LinkerMapFile.Parse(LoadFixture());

        Assert.Equal(39, map.Modules.Count);
        Assert.Contains(map.Modules, m => m.Name == "main.o");
        Assert.Contains(map.Modules, m => m.Name == @"C:\CC65\LIB/c64.lib(crt0.o)");
    }

    [Fact]
    public void Parse_AModulesSegments_HaveTheirOffsSizeAlignFillFields()
    {
        var map = LinkerMapFile.Parse(LoadFixture());

        var mainModule = map.Modules.Single(m => m.Name == "main.o");
        var codeSegment = Assert.Single(mainModule.Segments);
        Assert.Equal("CODE", codeSegment.Name);
        Assert.Equal(0, codeSegment.Offset);
        Assert.Equal(0x16, codeSegment.Size);
        Assert.Equal(1, codeSegment.Align);
        Assert.Equal(0, codeSegment.Fill);
    }

    [Fact]
    public void Parse_AllowsAModuleWithNoSegments_LikeALibraryMemberThatOnlyContributedSymbols()
    {
        var map = LinkerMapFile.Parse(LoadFixture());

        var clrscrModule = map.Modules.Single(m => m.Name == @"C:\CC65\LIB/c64.lib(clrscr.o)");
        Assert.Empty(clrscrModule.Segments);
    }

    [Fact]
    public void Parse_FindsEverySegment_WithItsStartEndSizeAlign()
    {
        var map = LinkerMapFile.Parse(LoadFixture());

        Assert.Equal(10, map.Segments.Count);
        var codeSegment = map.Segments.Single(s => s.Name == "CODE");
        Assert.Equal(0x000840, codeSegment.Start);
        Assert.Equal(0x000BA9, codeSegment.End);
        Assert.Equal(0x00036A, codeSegment.Size);
        Assert.Equal(1, codeSegment.Align);
    }

    [Fact]
    public void Parse_FindsEveryExport_EvenThoughTwoAreListedPerLine()
    {
        var map = LinkerMapFile.Parse(LoadFixture());

        Assert.Equal(62, map.Exports.Count);
        var bsout = map.Exports.Single(e => e.Name == "BSOUT");
        Assert.Equal(0x00FFD2, bsout.Value);
        Assert.Equal("RLA", bsout.Flags);
    }

    [Fact]
    public void Parse_DoesNotDuplicateExports_FromTheExportsListByValueSection()
    {
        // "Exports list by value:" repeats the exact same export set as "Exports list by name:",
        // just sorted by address instead - it must be skipped, not parsed into a second copy.
        var map = LinkerMapFile.Parse(LoadFixture());

        Assert.Equal(1, map.Exports.Count(e => e.Name == "BSOUT"));
    }

    [Fact]
    public void Parse_FindsEveryImport_WithItsReferences()
    {
        var map = LinkerMapFile.Parse(LoadFixture());

        Assert.Equal(62, map.Imports.Count);
        var bsout = map.Imports.Single(i => i.SymbolName == "BSOUT");
        Assert.Equal("kernal.o", bsout.DefiningModule);
        var reference = Assert.Single(bsout.References);
        Assert.Equal("crt0.o", reference.Module);
        Assert.Equal("c64/crt0.s", reference.File);
        Assert.Equal(10, reference.Line);
    }

    [Fact]
    public void Parse_HandlesLinkerGeneratedImports_WhoseModuleNameContainsASpace()
    {
        // "[linker generated]" (the module name, not a file path) has an internal space, which
        // would break a naive "split on first space" reference-line parser.
        var map = LinkerMapFile.Parse(LoadFixture());

        var headerLast = map.Imports.Single(i => i.SymbolName == "__HEADER_LAST__");
        Assert.Equal("[linker generated]", headerLast.DefiningModule);
        Assert.Equal(2, headerLast.References.Count);
        Assert.All(headerLast.References, r =>
        {
            Assert.Equal("[linker generated]", r.Module);
            Assert.Equal(@"C:\CC65\CFG/c64.cfg", r.File);
            Assert.Equal(14, r.Line);
        });
    }

    [Fact]
    public void Parse_RecordsTheMapFileLineNumber_ForEachExport()
    {
        var map = LinkerMapFile.Parse(LoadFixture());
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HelloCBM.lnk.map");
        var lines = File.ReadAllLines(fixturePath);

        var bsout = map.Exports.Single(e => e.Name == "BSOUT");

        Assert.Equal("BSOUT", lines[bsout.MapFileLineNumber - 1].TrimStart().Split(' ')[0]);
    }
}
