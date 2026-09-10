using Tedide.Core.Debugging;

namespace Tedide.Core.Tests.Debugging;

public class DbgFileTests
{
    // Real cc65/ld65 debug info, captured by building samples/HelloCBM with the Linker tab's
    // "Generate debug info" toggle on - see DbgFile's own doc comment for why a real file is used
    // rather than a hand-written one.
    private static string LoadFixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "HelloCBM.dbg"));

    [Fact]
    public void Parse_ReadsTheVersionRecord()
    {
        var dbg = DbgFile.Parse(LoadFixture());

        Assert.Equal(2, dbg.Version.Major);
        Assert.Equal(0, dbg.Version.Minor);
    }

    [Fact]
    public void Parse_FindsEveryModule()
    {
        var dbg = DbgFile.Parse(LoadFixture());

        var mainModule = dbg.Modules.Single(m => m.Name == "main.o");
        Assert.Equal(0, mainModule.File);
        Assert.Null(mainModule.Lib);
    }

    [Fact]
    public void Parse_ParsesAPlusJoinedFieldIntoMultipleModuleIds()
    {
        // A header shared by several translation units (e.g. a common runtime include) lists every
        // module id that read it as "mod=0+1+2+3+4", not a single value.
        var dbg = DbgFile.Parse(LoadFixture());

        var sharedHeader = dbg.Files.Single(f => f.Name == @"C:\CC65/asminc/longbranch.mac");
        Assert.Equal([0, 1, 2, 3, 4], sharedHeader.ModuleIds);
    }

    [Fact]
    public void Parse_ParsesHexAndDecimalNumberFields()
    {
        var dbg = DbgFile.Parse(LoadFixture());

        var codeSegment = dbg.Segments.Single(s => s.Name == "CODE");
        Assert.Equal(0x840, codeSegment.Start);
        Assert.Equal(0x36A, codeSegment.Size);

        var mainFile = dbg.Files.Single(f => f.Name == "src/main.c");
        Assert.Equal(821, mainFile.Size);
    }

    [Fact]
    public void Parse_LeavesSymbolValueNull_ForAnUnresolvedImport()
    {
        var dbg = DbgFile.Parse(LoadFixture());

        var import = dbg.Symbols.First(s => s.Type == "imp");
        Assert.Null(import.Value);
    }

    [Fact]
    public void FindAddressForSourceLine_ResolvesToTheSameAddressAsTheMainSymbol()
    {
        var dbg = DbgFile.Parse(LoadFixture());
        // "_main" appears twice: the defining label (type=lab, with a real address) in the module
        // that implements it, and an unresolved import (type=imp, no value) in whichever module
        // calls it (crt0) - the label is the one with the address this test wants.
        var mainSymbol = dbg.Symbols.Single(s => s.Name == "_main" && s.Type == "lab");

        var address = dbg.FindAddressForSourceLine("src/main.c", 19);

        Assert.Equal(mainSymbol.Value, address);
    }

    [Fact]
    public void FindAddressForSourceLine_MatchesByBareFileName_WhenTheExactPathDiffers()
    {
        var dbg = DbgFile.Parse(LoadFixture());

        var address = dbg.FindAddressForSourceLine(@"C:\GitHub\Tedide\samples\HelloCBM\src\main.c", 19);

        Assert.NotNull(address);
    }

    [Fact]
    public void FindAddressForSourceLine_ReturnsNull_ForALineWithNoCompiledCode()
    {
        var dbg = DbgFile.Parse(LoadFixture());

        // Line 1 of main.c is a #include, not a statement - never associated with any span.
        var address = dbg.FindAddressForSourceLine("src/main.c", 1);

        Assert.Null(address);
    }

    [Fact]
    public void FindSourceLocationForAddress_RoundTripsBackToTheSameLine()
    {
        var dbg = DbgFile.Parse(LoadFixture());
        var mainSymbol = dbg.Symbols.Single(s => s.Name == "_main" && s.Type == "lab");

        var location = dbg.FindSourceLocationForAddress(mainSymbol.Value!.Value);

        Assert.NotNull(location);
        Assert.Equal("src/main.c", location.Value.FilePath);
        Assert.Equal(19, location.Value.Line);
    }

    [Fact]
    public void FindSourceLocationForAddress_ReturnsNull_ForAnAddressOutsideEverySegment()
    {
        var dbg = DbgFile.Parse(LoadFixture());

        var location = dbg.FindSourceLocationForAddress(0xFFFF);

        Assert.Null(location);
    }
}
