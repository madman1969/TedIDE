using Cc65DocsDbBuilder;

namespace Tedide.DocViewer.Tests;

/// <summary>
/// The Plus/4 and C16 memory map page, generated from cc65's own headers (copied into the docs
/// builder's SourceHtml/Cc65Defs/) - each register at the address its struct offset gives it.
/// </summary>
public class Plus4MemoryMapTests
{
    [Fact]
    public void ParseStruct_GivesEachFieldItsOffset_CountingArrays()
    {
        var fields = Plus4MemoryMapGenerator.ParseStruct("""
            struct __chip {
                unsigned char       lo;             /* Low byte */
                unsigned char       gap[3];         /* Unused */
                unsigned char       ctrl;           /* Control */
            };
            """, "__chip");

        Assert.Equal(
            [new(0, 1, "lo", "Low byte"), new(1, 3, "gap", "Unused"), new(4, 1, "ctrl", "Control")],
            fields);
    }

    [Fact]
    public void ParseAsmInclude_TagsSections_AndSkipsAliases()
    {
        var symbols = Plus4MemoryMapGenerator.ParseAsmInclude("""
            ; ---------------------------------------------------------------------------
            ; Zero page

            STATUS          := $90          ; Kernal I/O completion status
            CHARCOLOR       := $53B
            BUF_LEN         = 89            ; Maximum length

            ; ---------------------------------------------------------------------------
            ; I/O

            TED_ROMSEL      := $FF3E
            ENABLE_ROM      := TED_ROMSEL
            """);

        Assert.Equal(
            [
                new("Zero page", "STATUS", "$90", true, "Kernal I/O completion status"),
                new("Zero page", "CHARCOLOR", "$53B", true, ""),
                new("Zero page", "BUF_LEN", "89", false, "Maximum length"),
                new("I/O", "TED_ROMSEL", "$FF3E", true, ""),
            ],
            symbols);
    }

    [Fact]
    public void RealTedStruct_SpansFF00ToFF3F()
    {
        var ted = Plus4MemoryMapGenerator.ParseStruct(File.ReadAllText(Path.Combine(DefsDir(), "_ted.h")), "__ted");

        Assert.Equal(0x40, ted.Sum(f => f.Length));
        Assert.Equal(new Plus4MemoryMapGenerator.StructField(0x15, 1, "bgcolor", "Background color"), ted.Single(f => f.Name == "bgcolor"));
        Assert.Equal(0x3E, ted.Single(f => f.Name == "enable_rom").Offset);
    }

    [Fact]
    public void DocsDb_HasTheMemoryMap_AfterThePlus4Page_WithItsLicence()
    {
        using var database = new DocDatabase(DocsDbPath());
        var platform = database.LoadCatalog().Single(b => b.Name == "cc65 Manual")
            .Categories.Single(c => c.Name == "Platform-Specific Information").Entries.Select(e => e.FileName).ToList();
        Assert.Equal(platform.IndexOf("plus4") + 1, platform.IndexOf(Plus4MemoryMapGenerator.PageId));

        var markdown = database.GetMarkdown(Plus4MemoryMapGenerator.PageId);
        Assert.StartsWith("# Plus/4 and C16 memory map\n", markdown);
        Assert.Contains("| $FF15 | `bgcolor` | `TED_BGCOLOR` | Background color |", markdown);
        Assert.Contains("| $FF20-$FF3D | `unused[30]` |  | Unused |", markdown);
        Assert.Contains("| $FD00 | `data` | Data register |", markdown);
        Assert.Contains("| $90 | `STATUS` | Kernal I/O completion status |", markdown);
        Assert.Contains("| $0314 | `IRQVec` |  |", markdown);
        Assert.DoesNotContain("ENABLE_ROM", markdown);
        Assert.Contains("(C) 2003 Ullrich von Bassewitz", markdown);
        Assert.Contains("> 3. This notice may not be removed or altered from any source distribution.", markdown);
    }

    private static string DefsDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "Cc65DocsDbBuilder", "SourceHtml", "Cc65Defs"));

    private static string DocsDbPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Docs.db");
        return File.Exists(beside)
            ? beside
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tedide.DocViewer", "Docs.db"));
    }
}
