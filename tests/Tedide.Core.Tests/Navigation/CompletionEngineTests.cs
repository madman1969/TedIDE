using Tedide.Core.Navigation;

namespace Tedide.Core.Tests.Navigation;

/// <summary>
/// <see cref="CompletionEngine"/> over a real <see cref="SymbolIndex"/> of a small project in a
/// temporary folder: main.c includes the project's game.h and a stand-in for cc65's conio.h; sound.s
/// is assembly.
/// </summary>
public sealed class CompletionEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-complete-").FullName;
    private readonly SymbolIndex _index = new();

    private const string GameH = """
        #ifndef GAME_H
        #define GAME_H
        #define MAX_SPRITES 8
        #define CLAMP(v, lo, hi) ((v) < (lo) ? (lo) : (v))
        typedef unsigned char byte;
        struct point { int x, y; };
        typedef struct { struct point pos; byte frame; } sprite_t;
        typedef struct player { sprite_t *sprite; byte lives; } player_t;
        extern player_t players[2];
        void draw_sprite(sprite_t *s, byte colour);
        player_t *find_player(byte id);
        #endif
        """;

    private const string ConioH = """
        void cputc(char c);
        void cputs(const char *s);
        unsigned char wherex(void);
        """;

    private const string SoundS = """
        .include "zp.inc"
        .export _play_note
        NOTE_COUNT = 12
        .proc play_note
        @loop:  dex
                bne @loop
                rts
        .endproc
        .macro beep
                nop
        .endmacro
        """;

    private const string ZpInc = """
        .importzp sp, ptr1
        """;

    public CompletionEngineTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "include"));
        Directory.CreateDirectory(Path.Combine(_dir, "cc65", "include"));
        Directory.CreateDirectory(Path.Combine(_dir, "cc65", "asminc"));
        File.WriteAllText(Path.Combine(_dir, "include", "game.h"), GameH);
        File.WriteAllText(Path.Combine(_dir, "cc65", "include", "conio.h"), ConioH);
        File.WriteAllText(Path.Combine(_dir, "cc65", "asminc", "zp.inc"), ZpInc);
        File.WriteAllText(SoundPath, SoundS);
        File.WriteAllText(OtherPath, "int high_score;\nvoid save_score(void) { }\n");
        _index.Configure([MainPath, SoundPath, OtherPath, Path.Combine(_dir, "include", "game.h")],
            [Path.Combine(_dir, "include")],
            [Path.Combine(_dir, "cc65", "include"), Path.Combine(_dir, "cc65", "asminc")],
            ["__C64__"]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MainPath => Path.Combine(_dir, "main.c");
    private string SoundPath => Path.Combine(_dir, "sound.s");
    private string OtherPath => Path.Combine(_dir, "score.c");

    /// <summary>Indexes main.c with <paramref name="text"/> (the "|" marks the caret, and is taken
    /// out) and asks for completions there.</summary>
    private IReadOnlyList<CompletionCandidate> CompleteMain(string text, bool requested = false)
    {
        var caret = text.IndexOf('|');
        text = text.Remove(caret, 1);
        File.WriteAllText(MainPath, "");
        _index.Update(MainPath, text);
        _index.Refresh();
        var prefixStart = caret;
        while (prefixStart > 0 && (char.IsAsciiLetterOrDigit(text[prefixStart - 1]) || text[prefixStart - 1] == '_'))
            prefixStart--;
        return CompletionEngine.Complete(_index, MainPath, text, caret, text[prefixStart..caret], requested);
    }

    private IReadOnlyList<CompletionCandidate> CompleteSound(string text, bool requested = false, string cpu = "6502")
    {
        var caret = text.IndexOf('|');
        text = text.Remove(caret, 1);
        _index.Update(SoundPath, text);
        _index.Refresh();
        var prefixStart = caret;
        while (prefixStart > 0 && (char.IsAsciiLetterOrDigit(text[prefixStart - 1]) || text[prefixStart - 1] == '_'))
            prefixStart--;
        return CompletionEngine.Complete(_index, SoundPath, text, caret, text[prefixStart..caret], requested, cpu);
    }

    private static List<string> Names(IReadOnlyList<CompletionCandidate> candidates) => candidates.Select(c => c.Name).ToList();

    private const string MainHead = "#include \"game.h\"\n#include <conio.h>\n\nvoid main(void)\n{\n    player_t *p = find_player(0);\n    byte count;\n";

    [Fact]
    public void Names_ComeFromLocalsThisFileItsIncludesAndTheProject_NearestFirst()
    {
        var found = CompleteMain(MainHead + "    c|\n}\n", requested: true);

        Assert.Equal(["count", "cputc", "cputs"], Names(found).Take(3));  // the local first
        Assert.Contains(found, c => c.Name == "count" && c.Kind == "local");
        Assert.Contains("const", Names(found));  // a keyword
        Assert.Contains("high_score", Names(CompleteMain(MainHead + "    hi|\n}\n")));  // another project file
    }

    [Fact]
    public void AHeaderNotIncluded_IsntOffered()
    {
        var found = CompleteMain("void main(void)\n{\n    cp|\n}\n");

        Assert.DoesNotContain("cputc", Names(found));
    }

    [Fact]
    public void Suggestions_WaitForTwoLetters_UnlessAskedFor()
    {
        Assert.Empty(CompleteMain(MainHead + "    c|\n}\n"));
        Assert.Contains("count", Names(CompleteMain(MainHead + "    c|\n}\n", requested: true)));
    }

    [Theory]
    [InlineData("    // co|\n")]
    [InlineData("    /* co|\n")]
    [InlineData("    cputs(\"co|")]
    [InlineData("    count = 12co|")]
    public void NothingIsOffered_InCommentsStringsOrNumbers(string line) =>
        Assert.Empty(CompleteMain(MainHead + line + "\n}\n", requested: true));

    [Fact]
    public void AnInclude_OffersNoSymbols() =>
        Assert.Empty(CompleteMain("#include <co|\n", requested: true));

    [Fact]
    public void Members_FollowTypedefsPointersAndArrays()
    {
        Assert.Equal(["lives", "sprite"], Names(CompleteMain(MainHead + "    p->|\n}\n")));
        Assert.Equal(["frame", "pos"], Names(CompleteMain(MainHead + "    p->sprite->|\n}\n")));
        Assert.Equal(["x", "y"], Names(CompleteMain(MainHead + "    p->sprite->pos.|\n}\n")));
        Assert.Equal(["lives"], Names(CompleteMain(MainHead + "    players[1].li|\n}\n")));
        Assert.Equal(["frame", "pos"], Names(CompleteMain(MainHead + "    find_player(1)->sprite->|\n}\n")));
        Assert.Equal(["lives", "sprite"], Names(CompleteMain(MainHead + "    (*p).|\n}\n")));
    }

    [Fact]
    public void AnUnknownLeftSide_OffersNoMembers() =>
        Assert.Empty(CompleteMain(MainHead + "    nothing.|\n}\n"));

    [Fact]
    public void AfterStruct_OnlyTagsAreOffered()
    {
        var found = CompleteMain(MainHead + "    struct p|\n}\n", requested: true);

        Assert.Equal(["player", "point"], Names(found));
    }

    [Fact]
    public void Signatures_ShowTheArgumentTheCaretIsIn()
    {
        var text = MainHead + "    draw_sprite(p->sprite, count|\n}\n";
        var caret = text.IndexOf('|');
        text = text.Remove(caret, 1);
        _index.Update(MainPath, text);
        _index.Refresh();

        var hint = CompletionEngine.SignatureAt(_index, MainPath, text, caret)!;
        Assert.Equal("void draw_sprite(sprite_t *s, byte colour)", hint.Text);
        Assert.Equal("byte colour", hint.Text.Substring(hint.ActiveStart, hint.ActiveLength));

        var macro = CompletionEngine.SignatureAt(_index, MainPath, MainHead + "    CLAMP(count, ", (MainHead + "    CLAMP(count, ").Length)!;
        Assert.Equal("lo", macro.Text.Substring(macro.ActiveStart, macro.ActiveLength));

        Assert.Null(CompletionEngine.SignatureAt(_index, MainPath, text, text.IndexOf("void main", StringComparison.Ordinal)));
        Assert.Null(CompletionEngine.SignatureAt(_index, MainPath, MainHead + "    if (count", (MainHead + "    if (count").Length));
    }

    [Fact]
    public void Assembly_OffersDirectivesAfterADot_InTheCaseTyped()
    {
        Assert.Contains("proc", Names(CompleteSound(".pr|")));
        Assert.Contains("PROC", Names(CompleteSound(".PR|")));
    }

    [Fact]
    public void Assembly_OffersInstructionsAndMacrosAtTheStartOfALine_ForTheProjectsCpu()
    {
        var found = Names(CompleteSound(SoundS + "\n        b|", requested: true));

        Assert.Contains("bne", found);
        Assert.Contains("beep", found);
        Assert.DoesNotContain("bra", found);
        Assert.Contains("bra", Names(CompleteSound(SoundS + "\n        b|", requested: true, cpu: "65C02")));
        Assert.Contains("sta", Names(CompleteSound(SoundS + "\nlabel:  st|")));  // after a label too
    }

    [Fact]
    public void Assembly_OperandsAreLabelsConstantsImportsAndCSymbols()
    {
        var found = CompleteSound(SoundS + "\n        lda #|", requested: true);

        Assert.Contains("NOTE_COUNT", Names(found));
        Assert.Contains("play_note", Names(found));
        Assert.Contains("ptr1", Names(found));  // from the .include
        Assert.Contains(found, c => c.Name == "_high_score" && c.Kind == "C variable");
        Assert.Empty(CompleteSound(SoundS + "\n        lda #1 ; NO|", requested: true));
    }

    [Fact]
    public void Assembly_OffersCheapLocalsInScope()
    {
        var text = SoundS.Replace("bne @loop", "bne @l|");

        Assert.Equal(["loop"], Names(CompleteSound(text)));
    }
}
