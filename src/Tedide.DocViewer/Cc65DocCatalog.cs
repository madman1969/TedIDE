namespace Tedide.DocViewer;

/// <summary>One manual page bundled under Cc65Docs/ - <see cref="FileName"/> is both its embedded
/// resource name and the key <see cref="Cc65DocLoader"/> loads it by.</summary>
public sealed record Cc65DocEntry(string FileName, string Description);

/// <summary>A group of <see cref="Cc65DocEntry"/> shown together in <see cref="DocViewerShell"/>'s
/// tree, e.g. all platform-specific pages under one node.</summary>
public sealed record Cc65DocCategory(string Name, IReadOnlyList<Cc65DocEntry> Entries);

/// <summary>
/// The bundled cc65 manual pages, grouped the same way as the "Program documentation" / "Usage" /
/// "Library information and other references" / "Platform-specific information" sections of
/// https://cc65.github.io/doc/ (the page these entries and descriptions were transcribed from) -
/// minus that page's one external, non-cc65 link (the o65 file format spec at www.6502.org), which
/// has no corresponding bundled file.
/// </summary>
public static class Cc65DocCatalog
{
    public static readonly IReadOnlyList<Cc65DocCategory> Categories =
    [
        new Cc65DocCategory("Program Documentation",
        [
            new Cc65DocEntry("ar65", "Describes the ar65 archiver."),
            new Cc65DocEntry("ca65", "Describes the ca65 macro assembler."),
            new Cc65DocEntry("cc65", "Describes the cc65 C compiler."),
            new Cc65DocEntry("chrcvt65", "Describes the vector font converter."),
            new Cc65DocEntry("cl65", "Describes the cl65 compile & link utility."),
            new Cc65DocEntry("co65", "Describes the co65 object-file converter."),
            new Cc65DocEntry("da65", "Describes the da65 6502/65C02 disassembler."),
            new Cc65DocEntry("grc65", "Describes the GEOS resource compiler."),
            new Cc65DocEntry("ld65", "Describes the ld65 linker."),
            new Cc65DocEntry("od65", "Describes the od65 object-file analyzer."),
            new Cc65DocEntry("sim65", "Describes the 6502 and 65C02 simulator."),
            new Cc65DocEntry("sp65", "Describes the sprite and bitmap utility."),
        ]),
        new Cc65DocCategory("Usage",
        [
            new Cc65DocEntry("intro", "Describes the use of the tools, by building a short \"hello world\" example."),
            new Cc65DocEntry("coding", "Contains hints on creating the most effective code with cc65."),
            new Cc65DocEntry("cc65-intern", "Describes internal details of cc65: linker configuration, calling conventions, etc."),
            new Cc65DocEntry("using-make", "Build programs, using the GNU Make utility."),
            new Cc65DocEntry("customizing", "How to use the cc65 toolset for a custom hardware platform (a target system not currently supported by the cc65 library set)."),
            new Cc65DocEntry("debugging", "Debug programs, using the VICE emulator."),
            new Cc65DocEntry("decompression", "Information about 6502-friendly decompressors shipped in cc65's runtime."),
        ]),
        new Cc65DocCategory("Library Information & References",
        [
            new Cc65DocEntry("funcref", "A (currently incomplete) function reference."),
            new Cc65DocEntry("dio", "Low-level disk I/O API."),
            new Cc65DocEntry("tgi", "Tiny Graphics Interface."),
            new Cc65DocEntry("geos", "The GEOSLib manual."),
            new Cc65DocEntry("library", "An overview over the cc65 runtime and C libraries."),
            new Cc65DocEntry("smc", "Describes Christian Krüger's macro package for writing self modifying assembler code."),
        ]),
        new Cc65DocCategory("Platform-Specific Information",
        [
            new Cc65DocEntry("agat", "Topics specific to the Agat machines."),
            new Cc65DocEntry("apple2", "Topics specific to the Apple ][."),
            new Cc65DocEntry("apple2enh", "Topics specific to the enhanced Apple //e."),
            new Cc65DocEntry("atari", "Topics specific to the Atari 8-bit machines."),
            new Cc65DocEntry("atari2600", "Topics specific to the Atari 2600 Game Console."),
            new Cc65DocEntry("atari5200", "Topics specific to the Atari 5200 Game Console."),
            new Cc65DocEntry("atari7800", "Topics specific to the Atari 7800 Game Console."),
            new Cc65DocEntry("atmos", "Topics specific to the Oric Atmos."),
            new Cc65DocEntry("c128", "Topics specific to the Commodore 128."),
            new Cc65DocEntry("c16", "Topics specific to the Commodore 16/116."),
            new Cc65DocEntry("c64", "Topics specific to the Commodore 64."),
            new Cc65DocEntry("cbm510", "Topics specific to the Commodore 510."),
            new Cc65DocEntry("cbm610", "Topics specific to the Commodore 610."),
            new Cc65DocEntry("creativision", "Topics specific to the Creativision Console."),
            new Cc65DocEntry("cx16", "Topics specific to the Commander X16."),
            new Cc65DocEntry("gamate", "Topics specific to the Bit Corporation Gamate Console."),
            new Cc65DocEntry("kim1", "Topics specific to the MOS Technology KIM-1."),
            new Cc65DocEntry("lynx", "Topics specific to the Atari Lynx Game Console."),
            new Cc65DocEntry("nes", "Topics specific to the Nintendo Entertainment System."),
            new Cc65DocEntry("osi", "Topics specific to the Ohio Scientific machines."),
            new Cc65DocEntry("pce", "Topics specific to the NEC PC-Engine (TurboGrafx-16) Console."),
            new Cc65DocEntry("pet", "Topics specific to the Commodore PET machines."),
            new Cc65DocEntry("plus4", "Topics specific to the Commodore Plus/4."),
            new Cc65DocEntry("rp6502", "Topics specific to the Picocomputer 6502."),
            new Cc65DocEntry("supervision", "Topics specific to the Watara Supervision Console."),
            new Cc65DocEntry("sym1", "Topics specific to the Synertek Systems Sym-1."),
            new Cc65DocEntry("telestrat", "Topics specific to the Oric Telestrat."),
            new Cc65DocEntry("vic20", "Topics specific to the Commodore VIC20."),
        ]),
    ];

    private static readonly IReadOnlyDictionary<string, Cc65DocEntry> ByFileName =
        Categories.SelectMany(c => c.Entries).ToDictionary(e => e.FileName);

    /// <summary>Every bundled page's <see cref="Cc65DocEntry.FileName"/> - used by
    /// <see cref="DocTextConverter.Convert"/> to tell an internal hyperlink (one of these) from a
    /// dead one (an href to a page cc65's docs don't ship).</summary>
    public static readonly IReadOnlySet<string> AllFileNames = ByFileName.Keys.ToHashSet();

    /// <summary>Looks up the entry a <see cref="DocLink.TargetFileName"/> points to, e.g. to select
    /// it in <see cref="DocViewerShell"/>'s tree when the user follows the link.</summary>
    public static bool TryGetEntry(string fileName, out Cc65DocEntry entry) =>
        ByFileName.TryGetValue(fileName, out entry!);
}
