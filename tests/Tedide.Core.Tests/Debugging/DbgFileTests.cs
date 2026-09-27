using Tedide.Core.Debugging;

namespace Tedide.Core.Tests.Debugging;

public class DbgFileTests
{
    // Real cc65/ld65 debug info, captured by building samples/HelloCBM with the Linker tab's
    // "Generate debug info" toggle on - see DbgFile's own doc comment for why a real file is used
    // rather than a hand-written one.
    private static string LoadFixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "HelloCBM.dbg"));

    // Real cc65/ld65 debug info from samples/CBMInfo, captured specifically because it has a case
    // HelloCBM.dbg doesn't: an address where a narrow assembly-only span and a wider C-paired span
    // both match (see FindSourceLocationForAddress_PrefersACSourceSpanOverAnOverlappingAssemblyOnlySpan).
    private static string LoadCBMInfoFixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CBMInfo.dbg"));

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
    public void FindSourceLocationForAddress_PrefersTheOriginalCSourceOverCl65sGeneratedAssembly()
    {
        var dbg = DbgFile.Parse(LoadFixture());

        // Span 7 carries two line records for the exact same compiled code: "file=0 (src/main.s),
        // line=62" and "file=2 (src/main.c), line=26" - the .s one sorts first in the fixture, so
        // resolving by address must actively prefer main.c or it silently points at a generated
        // file the user never sees on disk (the actual bug this test guards against).
        var address = dbg.FindAddressForSourceLine("src/main.c", 26);
        Assert.NotNull(address);

        var location = dbg.FindSourceLocationForAddress(address!.Value);

        Assert.NotNull(location);
        Assert.Equal("src/main.c", location.Value.FilePath);
        Assert.Equal(26, location.Value.Line);
    }

    [Fact]
    public void FindSourceLocationForAddress_PrefersACSourceSpanOverAnOverlappingAssemblyOnlySpan()
    {
        var dbg = DbgFile.Parse(LoadCBMInfoFixture());

        // Confirmed against this exact build: at CODE segment offset 154, span 108 ("src/main.s",
        // 2 bytes - a function's compiler-generated prologue) and span 116 ("src/main.c", 21 bytes
        // - the whole "int main(void)\n{" statement) both cover this address. A breakpoint set on
        // main.c's line 116 resolves to this same address (function entry), so a live debug session
        // stopping there was resolving back to "src/main.s" instead of the user's own "src/main.c" -
        // the real bug this test guards against (distinct from the same-span duplicate-line-record
        // case the HelloCBM-based test above covers: here it's the *span* choice itself, not just
        // which line record wins for one already-chosen span).
        var address = dbg.FindAddressForSourceLine("src/main.c", 116);
        Assert.NotNull(address);

        var location = dbg.FindSourceLocationForAddress(address!.Value);

        Assert.NotNull(location);
        Assert.Equal("src/main.c", location.Value.FilePath);
        Assert.Equal(116, location.Value.Line);
    }

    [Fact]
    public void FindEnclosingFunctionName_ResolvesTheFunctionAddressFallsInside()
    {
        var dbg = DbgFile.Parse(LoadCBMInfoFixture());
        var detectSystemSymbol = dbg.Symbols.Single(s => s.Name == "_detect_system" && s.Type == "lab");

        var name = dbg.FindEnclosingFunctionName(detectSystemSymbol.Value!.Value);

        Assert.Equal("detect_system", name);
    }

    [Fact]
    public void FindEnclosingFunctionName_ResolvesAnAddressPartwayThroughTheFunctionToo()
    {
        var dbg = DbgFile.Parse(LoadFixture());
        var mainSymbol = dbg.Symbols.Single(s => s.Name == "_main" && s.Type == "lab");

        // A function's own scope span covers its *entire* compiled range (unlike a source line's
        // much narrower span) - an address partway in, not just the very first byte, should still
        // resolve to the same function.
        var name = dbg.FindEnclosingFunctionName(mainSymbol.Value!.Value + 4);

        Assert.Equal("main", name);
    }

    [Fact]
    public void FindEnclosingFunctionName_ReturnsNull_ForAnAddressOutsideEveryFunctionScope()
    {
        var dbg = DbgFile.Parse(LoadFixture());

        var name = dbg.FindEnclosingFunctionName(0xFFFF);

        Assert.Null(name);
    }

    /// <summary>Every address in CBMInfo.dbg covered by a line record against cc65's runtime
    /// library macro file (asminc/generic.mac, at the path of the machine that built the
    /// library) - e.g. inside _heapmemavail, which memory.c calls.</summary>
    private static List<long> AddressesWithMacroFileLineRecords(DbgFile dbg)
    {
        var macroFiles = dbg.Files.Where(f => f.Name.EndsWith(".mac", StringComparison.OrdinalIgnoreCase)).Select(f => f.Id).ToHashSet();
        var macroSpans = dbg.Lines.Where(l => macroFiles.Contains(l.File)).SelectMany(l => l.Spans).ToHashSet();
        var segments = dbg.Segments.ToDictionary(s => s.Id);
        return dbg.Spans.Where(s => macroSpans.Contains(s.Id))
            .SelectMany(s => Enumerable.Range(0, (int)s.Size).Select(i => segments[s.Seg].Start + s.Start + i))
            .ToList();
    }

    [Fact]
    public void FindSourceLocationForAddress_NeverResolvesToTheRuntimeLibrarysMacroFiles()
    {
        // Resolving to "generic.mac:4" used to make Step Into stop inside _heapmemavail and move
        // the current-line highlight to line 4 of whatever file was open.
        var dbg = DbgFile.Parse(LoadCBMInfoFixture());
        var addresses = AddressesWithMacroFileLineRecords(dbg);
        Assert.NotEmpty(addresses);

        foreach (var address in addresses)
        {
            var location = dbg.FindSourceLocationForAddress(address);
            Assert.False(location is { } l && l.FilePath.EndsWith(".mac", StringComparison.OrdinalIgnoreCase),
                $"${address:X4} resolved to {location}");
        }
    }

    [Fact]
    public void FindProjectSourceLocationForAddress_ResolvesProjectCode_AndNotTheRuntimeLibrary()
    {
        var dbg = DbgFile.Parse(LoadCBMInfoFixture());
        var projectDirectory = Path.Combine(Path.GetTempPath(), "TedideDbgFileTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "src"));
        File.WriteAllText(Path.Combine(projectDirectory, "src", "main.c"), "");
        try
        {
            var main = dbg.Symbols.First(s => s.Name == "_main" && s.Type == "lab").Value!.Value;
            Assert.Equal("src/main.c", dbg.FindProjectSourceLocationForAddress(main, projectDirectory)?.FilePath);

            // The library's own .s files ("common/_heapmemavail.s") aren't in the project.
            Assert.All(AddressesWithMacroFileLineRecords(dbg),
                address => Assert.Null(dbg.FindProjectSourceLocationForAddress(address, projectDirectory)));
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Fact]
    public void FindProjectSourceLocationForAddress_ReturnsNull_WhenTheSourceFileIsntInTheProject()
    {
        var dbg = DbgFile.Parse(LoadCBMInfoFixture());
        var main = dbg.Symbols.First(s => s.Name == "_main" && s.Type == "lab").Value!.Value;

        Assert.Null(dbg.FindProjectSourceLocationForAddress(main, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void FindAssemblyLineForAddress_ResolvesCCodeToItsGeneratedAssemblyInstruction()
    {
        // The generated .s has a line record per instruction; this is how a stop is located
        // within GeneratedAssemblyFrames for the Locals table.
        var dbg = DbgFile.Parse(LoadCBMInfoFixture());
        var main = dbg.Symbols.First(s => s.Name == "_main" && s.Type == "lab").Value!.Value;

        var line = dbg.FindAssemblyLineForAddress(main);

        Assert.NotNull(line);
        Assert.EndsWith(".s", line.Value.FilePath);
        Assert.Null(dbg.FindAssemblyLineForAddress(0xFFFF));
    }

    [Fact]
    public void FindSourceLocationForAddress_ReturnsNull_ForAnAddressOutsideEverySegment()
    {
        var dbg = DbgFile.Parse(LoadFixture());

        var location = dbg.FindSourceLocationForAddress(0xFFFF);

        Assert.Null(location);
    }
}
