namespace Cc65DocsDbBuilder;

/// <summary>One manual page under <c>SourceHtml/</c> - <see cref="FileName"/> is both its source
/// file's base name (before <c>.html</c>) and the primary key it's stored under in Docs.db.</summary>
public sealed record PageEntry(string FileName, string Description);

/// <summary>A group of <see cref="PageEntry"/> shown together in Tedide.DocViewer's tree, e.g. all
/// platform-specific pages under one node - stored as a <c>Categories</c> row (see
/// <see cref="DocsDatabaseWriter"/>), in the order listed here.</summary>
public sealed record PageCategory(string Name, IReadOnlyList<PageEntry> Entries);

/// <summary>
/// The bundled cc65 manual pages, grouped the same way as the "Program documentation" / "Usage" /
/// "Library information and other references" / "Platform-specific information" sections of
/// https://cc65.github.io/doc/ (the page these entries and descriptions were transcribed from) -
/// minus that page's one external, non-cc65 link (the o65 file format spec at www.6502.org), which
/// has no corresponding bundled file.
/// </summary>
public static class PageCatalog
{
    public static readonly IReadOnlyList<PageCategory> Categories =
    [
        new PageCategory("Program Documentation",
        [
            new PageEntry("ar65", "Describes the ar65 archiver."),
            new PageEntry("ca65", "Describes the ca65 macro assembler."),
            new PageEntry("cc65", "Describes the cc65 C compiler."),
            new PageEntry("chrcvt65", "Describes the vector font converter."),
            new PageEntry("cl65", "Describes the cl65 compile & link utility."),
            new PageEntry("co65", "Describes the co65 object-file converter."),
            new PageEntry("da65", "Describes the da65 6502/65C02 disassembler."),
            new PageEntry("grc65", "Describes the GEOS resource compiler."),
            new PageEntry("ld65", "Describes the ld65 linker."),
            new PageEntry("od65", "Describes the od65 object-file analyzer."),
            new PageEntry("sim65", "Describes the 6502 and 65C02 simulator."),
            new PageEntry("sp65", "Describes the sprite and bitmap utility."),
        ]),
        new PageCategory("Usage",
        [
            new PageEntry("intro", "Describes the use of the tools, by building a short \"hello world\" example."),
            new PageEntry("coding", "Contains hints on creating the most effective code with cc65."),
            new PageEntry("cc65-intern", "Describes internal details of cc65: linker configuration, calling conventions, etc."),
            new PageEntry("using-make", "Build programs, using the GNU Make utility."),
            new PageEntry("customizing", "How to use the cc65 toolset for a custom hardware platform (a target system not currently supported by the cc65 library set)."),
            new PageEntry("debugging", "Debug programs, using the VICE emulator."),
            new PageEntry("decompression", "Information about 6502-friendly decompressors shipped in cc65's runtime."),
        ]),
        new PageCategory("Library Information & References",
        [
            new PageEntry("funcref", "A (currently incomplete) function reference."),
            new PageEntry("dio", "Low-level disk I/O API."),
            new PageEntry("tgi", "Tiny Graphics Interface."),
            new PageEntry("geos", "The GEOSLib manual."),
            new PageEntry("library", "An overview over the cc65 runtime and C libraries."),
            new PageEntry("smc", "Describes Christian Krüger's macro package for writing self modifying assembler code."),
        ]),
        new PageCategory("Platform-Specific Information",
        [
            new PageEntry("agat", "Topics specific to the Agat machines."),
            new PageEntry("apple2", "Topics specific to the Apple ][."),
            new PageEntry("apple2enh", "Topics specific to the enhanced Apple //e."),
            new PageEntry("atari", "Topics specific to the Atari 8-bit machines."),
            new PageEntry("atari2600", "Topics specific to the Atari 2600 Game Console."),
            new PageEntry("atari5200", "Topics specific to the Atari 5200 Game Console."),
            new PageEntry("atari7800", "Topics specific to the Atari 7800 Game Console."),
            new PageEntry("atmos", "Topics specific to the Oric Atmos."),
            new PageEntry("c128", "Topics specific to the Commodore 128."),
            new PageEntry("c16", "Topics specific to the Commodore 16/116."),
            new PageEntry("c64", "Topics specific to the Commodore 64."),
            new PageEntry("cbm510", "Topics specific to the Commodore 510."),
            new PageEntry("cbm610", "Topics specific to the Commodore 610."),
            new PageEntry("creativision", "Topics specific to the Creativision Console."),
            new PageEntry("cx16", "Topics specific to the Commander X16."),
            new PageEntry("gamate", "Topics specific to the Bit Corporation Gamate Console."),
            new PageEntry("kim1", "Topics specific to the MOS Technology KIM-1."),
            new PageEntry("lynx", "Topics specific to the Atari Lynx Game Console."),
            new PageEntry("nes", "Topics specific to the Nintendo Entertainment System."),
            new PageEntry("osi", "Topics specific to the Ohio Scientific machines."),
            new PageEntry("pce", "Topics specific to the NEC PC-Engine (TurboGrafx-16) Console."),
            new PageEntry("pet", "Topics specific to the Commodore PET machines."),
            new PageEntry("plus4", "Topics specific to the Commodore Plus/4."),
            new PageEntry("rp6502", "Topics specific to the Picocomputer 6502."),
            new PageEntry("supervision", "Topics specific to the Watara Supervision Console."),
            new PageEntry("sym1", "Topics specific to the Synertek Systems Sym-1."),
            new PageEntry("telestrat", "Topics specific to the Oric Telestrat."),
            new PageEntry("vic20", "Topics specific to the Commodore VIC20."),
        ]),
    ];

    public static readonly IReadOnlySet<string> AllFileNames =
        Categories.SelectMany(c => c.Entries).Select(e => e.FileName).ToHashSet();
}
