using Tedide.Core.Navigation;

namespace Tedide.Core.Tests.Navigation;

public class CodeNavigatorTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "tedide-nav-tests");
    private static readonly string MainC = Path.Combine(Root, "src", "main.c");
    private static readonly string ScreenC = Path.Combine(Root, "src", "screen.c");
    private static readonly string ScreenH = Path.Combine(Root, "include", "screen.h");
    private static readonly string BorderS = Path.Combine(Root, "src", "border.s");

    private const string ScreenHText = """
        #ifndef SCREEN_H
        #define SCREEN_H
        #define SCREEN_W 40
        typedef struct { unsigned char x, y; } point;
        void __fastcall__ draw_box(unsigned char width, unsigned char height);
        void border_flash(void);
        extern unsigned char frame_count;
        #endif
        """;

    private const string ScreenCText = """
        #include "screen.h"
        unsigned char frame_count;
        static unsigned char width = 3; /* draw_box in a comment */
        void __fastcall__ draw_box(unsigned char width, unsigned char height)
        {
            unsigned char i;
            for (i = 0; i < width; ++i) {
                unsigned char width = i;
                frame_count += width;
            }
            frame_count += height;
        }
        """;

    private const string MainCText = """
        #include <conio.h>
        #include "screen.h"
        int main(void)
        {
            point p;
            p.x = SCREEN_W;
            draw_box(p.x, 5);
            border_flash();
            printf("draw_box\n");
            return 0;
        }
        """;

    private const string BorderSText = """
                .export _border_flash
                .import _frame_count
        .proc _border_flash: near
                ldx #8
        @loop:  inc $d020
                dex
                bne @loop
        next:   inc _frame_count
        @loop:  rts
        .endproc
        """;

    private static CodeNavigator Navigator(IReadOnlyList<string>? libraryDirectories = null)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MainC] = MainCText,
            [ScreenC] = ScreenCText,
            [ScreenH] = ScreenHText,
            [BorderS] = BorderSText,
        };
        return new CodeNavigator(
            [.. files.Keys],
            path => files.TryGetValue(path, out var text) ? text : CodeNavigator.ReadFromDisk(path),
            [Path.Combine(Root, "include")],
            libraryDirectories);
    }

    /// <summary>The 1-based line and column of the <paramref name="occurrence"/>th (1-based) match of
    /// <paramref name="needle"/> in <paramref name="text"/>.</summary>
    private static (int Line, int Column) At(string text, string needle, int occurrence = 1)
    {
        var index = -1;
        for (var n = 0; n < occurrence; n++)
            index = text.IndexOf(needle, index + 1, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' #{occurrence} not found");
        var before = text[..index];
        var line = before.Count(c => c == '\n') + 1;
        return (line, index - (before.LastIndexOf('\n') + 1) + 1);
    }

    private static DefinitionResult Definition(CodeNavigator navigator, string file, string text, string needle, int occurrence = 1)
    {
        var (line, column) = At(text, needle, occurrence);
        return navigator.GoToDefinition(file, line, column);
    }

    [Fact]
    public void GoToDefinition_FromACall_FindsTheFunctionBodyRatherThanThePrototype()
    {
        var result = Definition(Navigator(), MainC, MainCText, "draw_box(p");

        var definition = Assert.Single(result.Definitions);
        Assert.Equal(SymbolKind.Function, definition.Kind);
        Assert.Equal(ScreenC, definition.FilePath);
        Assert.Equal(At(ScreenCText, "draw_box(unsigned").Line, definition.Line);
    }

    [Fact]
    public void GoToDefinition_OnTheDefinitionItself_GoesToThePrototype()
    {
        var result = Definition(Navigator(), ScreenC, ScreenCText, "draw_box(unsigned");

        var definition = Assert.Single(result.Definitions);
        Assert.Equal(SymbolKind.Prototype, definition.Kind);
        Assert.Equal(ScreenH, definition.FilePath);
    }

    [Fact]
    public void GoToDefinition_FromCToAssembly_FollowsTheUnderscorePrefix()
    {
        var result = Definition(Navigator(), MainC, MainCText, "border_flash();");

        var definition = Assert.Single(result.Definitions);
        Assert.Equal(BorderS, definition.FilePath);
        Assert.Equal(SymbolKind.Function, definition.Kind);
        Assert.Equal("_border_flash", definition.Name);
    }

    [Fact]
    public void GoToDefinition_FromAssemblyToC_DropsTheUnderscorePrefix()
    {
        var result = Definition(Navigator(), BorderS, BorderSText, "_frame_count", occurrence: 2);

        var definition = Assert.Single(result.Definitions);
        Assert.Equal(ScreenC, definition.FilePath);
        Assert.Equal(SymbolKind.Variable, definition.Kind);
    }

    [Fact]
    public void GoToDefinition_PrefersTheInnermostLocal()
    {
        var navigator = Navigator();

        // "frame_count += width;" inside the for body: the block's own local, not the parameter or the static.
        var inner = Assert.Single(Definition(navigator, ScreenC, ScreenCText, "width;", occurrence: 2).Definitions);
        Assert.Equal(SymbolKind.LocalVariable, inner.Kind);
        Assert.Equal(At(ScreenCText, "width = i").Line, inner.Line);

        // "i < width" in the for header: the parameter.
        var parameter = Assert.Single(Definition(navigator, ScreenC, ScreenCText, "width; ++i").Definitions);
        Assert.Equal(SymbolKind.Parameter, parameter.Kind);
    }

    [Fact]
    public void GoToDefinition_FindsMacrosTypedefsAndMembers()
    {
        var navigator = Navigator();

        Assert.Equal(SymbolKind.Macro, Assert.Single(Definition(navigator, MainC, MainCText, "SCREEN_W").Definitions).Kind);
        Assert.Equal(SymbolKind.Typedef, Assert.Single(Definition(navigator, MainC, MainCText, "point p").Definitions).Kind);

        var member = Assert.Single(Definition(navigator, MainC, MainCText, "x = SCREEN_W").Definitions);
        Assert.Equal(SymbolKind.Member, member.Kind);
        Assert.Equal(ScreenH, member.FilePath);
    }

    [Fact]
    public void GoToDefinition_FindsCheapLocalsWithinTheirOwnScope()
    {
        var navigator = Navigator();

        var first = Assert.Single(Definition(navigator, BorderS, BorderSText, "@loop", occurrence: 2).Definitions);
        Assert.Equal(At(BorderSText, "@loop:").Line, first.Line);
    }

    [Fact]
    public void GoToDefinition_OnAnInclude_OpensTheHeader()
    {
        var result = Definition(Navigator(), MainC, MainCText, "\"screen.h\"");

        var file = Assert.Single(result.Definitions);
        Assert.Equal(SymbolKind.File, file.Kind);
        Assert.Equal(Path.GetFullPath(ScreenH), file.FilePath);
    }

    [Fact]
    public void GoToDefinition_OnAnUnresolvableInclude_ExplainsWhy()
    {
        var result = Definition(Navigator(), MainC, MainCText, "conio.h");

        Assert.Empty(result.Definitions);
        Assert.Contains("conio.h", result.Message);
    }

    [Fact]
    public void GoToDefinition_FallsBackToCc65Headers_AndResolvesSystemIncludes()
    {
        var library = Path.Combine(Path.GetTempPath(), "tedide-nav-tests-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        try
        {
            File.WriteAllText(Path.Combine(library, "conio.h"), "int printf (const char* format, ...);\n");
            File.WriteAllText(Path.Combine(library, "stdio.h"), "int printf (const char* format, ...);\n");
            var navigator = Navigator([library]);

            // main.c includes conio.h, so its prototype wins over stdio.h's, which main.c never includes.
            var printf = Assert.Single(Definition(navigator, MainC, MainCText, "printf").Definitions);
            Assert.Equal(SymbolKind.Prototype, printf.Kind);
            Assert.Equal(Path.Combine(library, "conio.h"), printf.FilePath);

            var include = Assert.Single(Definition(navigator, MainC, MainCText, "conio.h").Definitions);
            Assert.Equal(Path.Combine(library, "conio.h"), include.FilePath);
        }
        finally
        {
            Directory.Delete(library, recursive: true);
        }
    }

    [Fact]
    public void GoToDefinition_FollowsTheTargetsIncludeChainIntoCc65Headers()
    {
        // A miniature of cc65's own layout: cbm.h includes only the current target's header.
        var library = Path.Combine(Path.GetTempPath(), "tedide-nav-tests-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(library);
        try
        {
            File.WriteAllText(Path.Combine(library, "cbm.h"), "#if defined(__C64__)\n#  include <c64.h>\n#elif defined(__VIC20__)\n#  include <vic20.h>\n#endif\n");
            File.WriteAllText(Path.Combine(library, "c64.h"), "#define COLOR_BLACK 0x00\n");
            File.WriteAllText(Path.Combine(library, "vic20.h"), "#define COLOR_BLACK 0x00\n");
            File.WriteAllText(Path.Combine(library, "pet.h"), "#define COLOR_BLACK 0x00\n");
            var main = Path.Combine(Root, "src", "colours.c");
            const string text = "#include <cbm.h>\nint c = COLOR_BLACK;\n";

            CodeNavigator ForTarget(string macro) => new(
                [main], path => path == main ? text : CodeNavigator.ReadFromDisk(path), [], [library], ["__CBM__", macro]);

            var c64 = Assert.Single(Definition(ForTarget("__C64__"), main, text, "COLOR_BLACK").Definitions);
            Assert.Equal(Path.Combine(library, "c64.h"), c64.FilePath);
            var vic20 = Assert.Single(Definition(ForTarget("__VIC20__"), main, text, "COLOR_BLACK").Definitions);
            Assert.Equal(Path.Combine(library, "vic20.h"), vic20.FilePath);
            // Nothing reachable for a PET: every header's definition is offered rather than none.
            Assert.Equal(3, Definition(ForTarget("__PET__"), main, text, "COLOR_BLACK").Definitions.Count);
        }
        finally
        {
            Directory.Delete(library, recursive: true);
        }
    }

    [Fact]
    public void GoToDefinition_PrefersTheActiveBranchOfAProjectsOwnIf()
    {
        var file = Path.Combine(Root, "src", "border.h");
        const string text = "#if defined(__C64__)\n#define BORDER 0xD020\n#else\n#define BORDER 0x900F\n#endif\nint b = BORDER;\n";
        CodeNavigator ForTarget(string macro) => new([file], _ => text, predefinedMacros: [macro]);

        Assert.Equal(2, Assert.Single(Definition(ForTarget("__C64__"), file, text, "BORDER;").Definitions).Line);
        Assert.Equal(4, Assert.Single(Definition(ForTarget("__VIC20__"), file, text, "BORDER;").Definitions).Line);
    }

    [Fact]
    public void Cc65LibraryDirectories_UsesCc65HomeOrTheCl65OnThePath()
    {
        var home = Path.Combine(Path.GetTempPath(), "tedide-nav-tests-cc65-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        Directory.CreateDirectory(Path.Combine(home, "include"));
        Directory.CreateDirectory(Path.Combine(home, "asminc"));
        File.WriteAllText(Path.Combine(home, "bin", "cl65.exe"), string.Empty);
        try
        {
            string[] expected = [Path.Combine(home, "include"), Path.Combine(home, "asminc")];
            Assert.Equal(expected, CodeNavigator.Cc65LibraryDirectories(home, null));
            Assert.Equal(expected, CodeNavigator.Cc65LibraryDirectories(null, $"C:\\nowhere{Path.PathSeparator}{Path.Combine(home, "bin")}"));
            Assert.Empty(CodeNavigator.Cc65LibraryDirectories(null, "C:\\nowhere"));
            Assert.Empty(CodeNavigator.Cc65LibraryDirectories(null, null));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void ReadFromDisk_ReturnsNullForAMissingFile()
    {
        Assert.Null(CodeNavigator.ReadFromDisk(Path.Combine(Root, "missing", "nothing.c")));
    }

    [Fact]
    public void GoToDefinition_ExplainsWhenThereIsNothingToFind()
    {
        var navigator = Navigator();

        Assert.Equal("No symbol at the cursor.", navigator.GoToDefinition(MainC, At(MainCText, "return").Line, 3).Message);
        Assert.Contains("No definition found", Definition(navigator, MainC, MainCText, "printf").Message);
        Assert.Contains("C and assembly", navigator.GoToDefinition(Path.Combine(Root, "readme.txt"), 1, 1).Message);
    }

    [Fact]
    public void FindReferences_AcrossCHeadersAndAssembly_SkipsCommentsAndStrings()
    {
        var result = Navigator().FindReferences(MainC, At(MainCText, "draw_box(p").Line, At(MainCText, "draw_box(p").Column);

        Assert.Equal("draw_box", result.Symbol);
        // Header prototype, screen.c definition, main.c call - not the comment or the printf string.
        Assert.Equal(3, result.References.Count);
        Assert.Equal(1, result.References.Count(r => r.IsDefinition && r.FilePath == ScreenC));

        var flash = Navigator().FindReferences(BorderS, At(BorderSText, "_border_flash: near").Line, At(BorderSText, "_border_flash: near").Column);
        // .export, .proc, the C prototype and the C call.
        Assert.Equal(4, flash.References.Count);
    }

    [Fact]
    public void FindReferences_OfALocal_StaysInsideItsScope()
    {
        var (line, column) = At(ScreenCText, "width = i");
        var result = Navigator().FindReferences(ScreenC, line, column);

        // The block local's declaration and its one use - not the parameter or the static.
        Assert.Equal(2, result.References.Count);
        Assert.All(result.References, r => Assert.InRange(r.Line, line, line + 1));
    }

    [Fact]
    public void FindReferences_OfAGlobal_SkipsLocalsThatHideIt()
    {
        var (line, column) = At(ScreenCText, "width = 3");
        var result = Navigator().FindReferences(ScreenC, line, column);

        Assert.Equal(line, Assert.Single(result.References).Line);
    }

    [Fact]
    public void FindReferences_SeparatesMembersFromVariables()
    {
        var (line, column) = At(MainCText, "x = SCREEN_W");
        var members = Navigator().FindReferences(MainC, line, column);

        // The member's own declaration in the header, and both "p.x" uses.
        Assert.Equal(3, members.References.Count);
    }

    private static RenamePlan Rename(string file, string text, string needle, string newName, int occurrence = 1)
    {
        var (line, column) = At(text, needle, occurrence);
        return Navigator().PlanRename(file, line, column, newName);
    }

    [Fact]
    public void PlanRename_FromC_RewritesTheAssemblySideWithItsUnderscore()
    {
        var plan = Rename(MainC, MainCText, "border_flash();", "flash_border");

        Assert.Null(plan.Error);
        Assert.Equal(3, plan.FileCount);
        Assert.Equal(2, plan.Edits.Count(e => e.FilePath == BorderS && e is { OldText: "_border_flash", NewText: "_flash_border" }));
        Assert.Contains(plan.Edits, e => e.FilePath == ScreenH && e.NewText == "flash_border");
        Assert.Contains(plan.Edits, e => e.FilePath == MainC && e.NewText == "flash_border");

        var renamed = RenamePlan.Apply(BorderSText, plan.Edits.Where(e => e.FilePath == BorderS));
        Assert.Contains(".export _flash_border", renamed);
        Assert.Contains(".proc _flash_border: near", renamed);
    }

    [Fact]
    public void PlanRename_FromAssembly_TakesTheUnderscoredName()
    {
        var plan = Rename(BorderS, BorderSText, "_border_flash: near", "_flash");

        Assert.Null(plan.Error);
        Assert.Contains(plan.Edits, e => e.FilePath == MainC && e.NewText == "flash");
        Assert.Contains("needs one too", Rename(BorderS, BorderSText, "_border_flash: near", "flash").Error);
    }

    [Fact]
    public void PlanRename_OfALocal_LeavesTheSameNamedGlobalAndParameterAlone()
    {
        var plan = Rename(ScreenC, ScreenCText, "width = i", "w");

        Assert.Null(plan.Error);
        Assert.Equal(2, plan.Edits.Count);
        var renamed = RenamePlan.Apply(ScreenCText, plan.Edits);
        Assert.Contains("unsigned char w = i;", renamed);
        Assert.Contains("frame_count += w;", renamed);
        Assert.Contains("static unsigned char width = 3;", renamed);
        Assert.Contains("i < width", renamed);
    }

    [Fact]
    public void PlanRename_OfACheapLocal_KeepsTheAtSign()
    {
        var plan = Rename(BorderS, BorderSText, "@loop", "@again", occurrence: 2);

        Assert.Null(plan.Error);
        Assert.Equal(2, plan.Edits.Count);
        Assert.Contains("must start with '@'", Rename(BorderS, BorderSText, "@loop", "again", occurrence: 2).Error);
        Assert.Contains("Only a cheap local", Rename(BorderS, BorderSText, "next:", "@next").Error);
    }

    [Theory]
    [InlineData("2fast", "isn't a valid name")]
    [InlineData("for", "C keyword")]
    [InlineData("draw_box", "already has")]
    [InlineData("frame_count", "already used")]
    [InlineData("SCREEN_W", "already used")]
    public void PlanRename_RefusesBadOrClashingNames(string newName, string expected)
    {
        var plan = Rename(MainC, MainCText, "draw_box(p", newName);

        Assert.Empty(plan.Edits);
        Assert.Contains(expected, plan.Error);
    }

    [Fact]
    public void PlanRename_RefusesAGlobalNameALocalWouldCapture()
    {
        // A global renamed to "i" would be hidden by draw_box's own local "i".
        var plan = Rename(ScreenC, ScreenCText, "frame_count;", "i");

        Assert.Contains("already used", plan.Error);
    }

    [Fact]
    public void PlanRename_RefusesSymbolsDefinedOutsideTheProject()
    {
        Assert.Contains("isn't defined in this project", Rename(MainC, MainCText, "printf", "print").Error);
        Assert.Equal("No symbol at the cursor.", Navigator().PlanRename(MainC, At(MainCText, "return").Line, 3, "x").Error);
    }

    [Fact]
    public void RenamePlanApply_RefusesTextThatHasChanged()
    {
        var edit = new TextEdit(MainC, 1, 1, "old", "new");

        Assert.Equal("new text", RenamePlan.Apply("old text", [edit]));
        Assert.Throws<InvalidDataException>(() => RenamePlan.Apply("odd text", [edit]));
        Assert.Throws<InvalidDataException>(() => RenamePlan.Apply("old", [edit with { Line = 5 }]));
    }

    [Fact]
    public void FindReferences_OfACheapLocal_StaysBetweenItsLabels()
    {
        var (line, column) = At(BorderSText, "@loop", occurrence: 2);
        var result = Navigator().FindReferences(BorderS, line, column);

        Assert.Equal(2, result.References.Count);
        Assert.Equal(1, result.References.Count(r => r.IsDefinition));
    }
}
