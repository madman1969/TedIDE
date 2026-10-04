using Tedide.Core.Navigation;

namespace Tedide.Core.Tests.Navigation;

/// <summary><see cref="DocumentOutline"/>: a file's symbols nested by the bodies that hold them.</summary>
public class DocumentOutlineTests
{
    private const string C = """
        #include <stdio.h>
        #define MAX 8
        #define CLAMP(v, lo, hi) ((v) < (lo) ? (lo) : (v))
        struct point { int x, y; };
        typedef struct {
            struct point pos;
            unsigned char frame;
        } sprite_t;
        enum color { RED, GREEN };
        static sprite_t sprites[MAX];
        void draw(sprite_t *s);

        void draw(sprite_t *s)
        {
            int local = 0;
            s->pos.x = local;
        }
        #if 0
        int unused;
        #endif

        """;

    private const string Asm = """
        .import _main
        BORDER = $D020
        .proc init
            lda #0
        @loop:
            sta BORDER
            bne @loop
        .endproc
        start:
        @wait:
            jmp @wait
        .struct Ball
            xpos .byte
        .endstruct

        """;

    private static IReadOnlyList<OutlineNode> Outline(string path, string text) =>
        DocumentOutline.Build(FileSymbols.Scan(path, text, new HashSet<string>(), (_, _, _) => null, DateTime.MaxValue));

    private static string Shape(IReadOnlyList<OutlineNode> nodes, string indent = "") =>
        string.Concat(nodes.Select(n => $"{indent}{n.Text}\n{Shape(n.Children, indent + "  ")}"));

    [Fact]
    public void C_NestsMembersAndConstants_AndLeavesOutLocals()
    {
        Assert.Equal("""
            MAX
            CLAMP(v, lo, hi)
            struct point
              x : int
              y : int
            sprite_t : {...}
              pos : struct point
              frame : unsigned char
            enum color
              RED
              GREEN
            sprites : sprite_t*
            draw(sprite_t *s) : void
            draw(sprite_t *s) : void
            unused : int

            """, Shape(Outline("t.c", C)));
    }

    [Fact]
    public void C_KnowsWhatEachNodeCovers_AndWhatsInactive()
    {
        var outline = Outline("t.c", C);

        var function = outline.Single(n => n.Kind == SymbolKind.Function);
        Assert.Equal((13, 17), (function.StartLine, function.EndLine));
        Assert.Same(function, DocumentOutline.At(outline, 16));
        Assert.Equal("frame : unsigned char", DocumentOutline.At(outline, 7)!.Text);
        Assert.Equal("sprite_t : {...}", DocumentOutline.At(outline, 5)!.Text);
        Assert.Null(DocumentOutline.At(outline, 12));
        Assert.True(outline.Single(n => n.Definition.Name == "unused").Inactive);
        Assert.False(function.Inactive);
    }

    [Fact]
    public void Assembly_NestsBlocksAndCheapLocals()
    {
        Assert.Equal("""
            _main
            BORDER
            init
              @loop
            start
              @wait
            Ball
              xpos

            """, Shape(Outline("t.s", Asm)));

        var outline = Outline("t.s", Asm);
        Assert.Equal((3, 8), (outline[2].StartLine, outline[2].EndLine));
        Assert.Equal("@loop", DocumentOutline.At(outline, 5)!.Text);
    }

    [Fact]
    public void Filter_KeepsMatchesAndTheirContainers()
    {
        var outline = Outline("t.c", C);

        Assert.Equal("""
            struct point
              x : int
              y : int
            sprite_t : {...}
              pos : struct point

            """, Shape(DocumentOutline.Filter(outline, " po")));  // "struct point" matches, so keeps all its members
        Assert.Same(outline, DocumentOutline.Filter(outline, "  "));
    }

    [Fact]
    public void SortedByName_SortsEachLevel()
    {
        var sorted = DocumentOutline.SortedByName(Outline("t.s", Asm));

        Assert.Equal(["Ball", "BORDER", "init", "start", "_main"], sorted.Select(n => n.Text));
    }
}
