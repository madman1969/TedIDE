namespace Cc65DocsDbBuilder;

/// <summary>
/// The chapters of the VICE manual (https://vice-emu.sourceforge.io/vice_toc.html) most relevant to
/// Tedide - running programs in the emulators, each machine's own options (including the PET's),
/// disk and file formats, the monitor, and the binary monitor protocol Tedide's debugger speaks -
/// plus the manual's own Copyright chapter and the GNU GPL it's distributed under. Each chapter is
/// one page of the manual's HTML (<c>vice_N.html</c>, kept under <c>SourceHtml/Vice/</c>); page ids
/// are <c>vice/</c> plus a readable name rather than the chapter number, since DocViewer's tree
/// shows the id. Converted by <see cref="ViceManualHtmlToMarkdownConverter"/>.
/// </summary>
public static class ViceManualPageCatalog
{
    public const string PageIdPrefix = "vice/";

    /// <summary>A bundled chapter: its source file's chapter number and its page id.</summary>
    public sealed record Chapter(int Number, string PageId, string Description);

    public static readonly IReadOnlyList<(string Category, IReadOnlyList<Chapter> Chapters)> Categories =
    [
        ("Using VICE",
        [
            new Chapter(1, "vice/about", "1 About VICE - what each emulator emulates"),
            new Chapter(2, "vice/invoking", "2 Invoking the emulators - command line and autostart"),
            new Chapter(7, "vice/machine_specific", "7 Machine-specific features - C64/128, VIC-20, Plus/4, PET, CBM-II"),
            new Chapter(10, "vice/media_images", "10 Media images - disk, tape and cartridge images"),
            new Chapter(17, "vice/file_formats", "17 The emulator file formats - D64, T64, PRG, CRT..."),
        ]),
        ("Debugging",
        [
            new Chapter(12, "vice/monitor", "12 Monitor - the built-in machine-language monitor"),
            new Chapter(13, "vice/binary_monitor", "13 Binary monitor - the protocol Tedide's debugger uses"),
        ]),
        ("Tools",
        [
            new Chapter(14, "vice/c1541", "14 c1541 - the disk image tool"),
            new Chapter(16, "vice/petcat", "16 petcat - BASIC program tokenizer/detokenizer"),
        ]),
        ("Licence",
        [
            new Chapter(19, "vice/copyright", "19 Copyright - VICE's authors and licence notice"),
            new Chapter(21, "vice/gpl", "21 GNU General Public License - the licence VICE and this manual are under"),
        ]),
    ];

    public static readonly IReadOnlyList<Chapter> AllChapters = Categories.SelectMany(c => c.Chapters).ToList();

    public static readonly IReadOnlySet<string> AllFileNames = AllChapters.Select(c => c.PageId).ToHashSet();

    /// <summary>The page id of chapter <paramref name="number"/>, or null if it isn't bundled.</summary>
    public static string? PageIdFor(int number) => AllChapters.FirstOrDefault(c => c.Number == number)?.PageId;
}
