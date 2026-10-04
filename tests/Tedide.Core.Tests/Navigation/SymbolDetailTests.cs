using Tedide.Core.Navigation;

namespace Tedide.Core.Tests.Navigation;

/// <summary><see cref="CSymbolScanner.ScanDetailed"/>: types, which struct a member belongs to, and
/// signatures - what code completion follows.</summary>
public class SymbolDetailTests
{
    private const string Source = """
        #define CLAMP(v, lo, hi) ((v) < (lo) ? (lo) : (v))
        typedef unsigned char byte;
        struct point { int x, y; };
        typedef struct { struct point pos; byte frame, *frames[4]; } sprite_t;
        typedef struct player *player_ptr;
        static sprite_t sprites[8], *current;
        extern const char *names[];
        struct player *find_player(byte id, const char *name);

        static void draw(sprite_t *s, unsigned char count)
        {
            struct point corner, *edge;
            s->pos.x = 0;
        }

        """;

    private static CScan Scan() => CSymbolScanner.ScanDetailed("t.c", SourceTokenizer.TokenizeC(Source));

    private static SymbolDetail Detail(CScan scan, string name, SymbolKind? kind = null) =>
        scan.Details[Assert.Single(scan.Definitions, d => d.Name == name && (kind is null || d.Kind == kind))];

    [Fact]
    public void Variables_ParametersAndLocals_HaveTheirTypes()
    {
        var scan = Scan();

        Assert.Equal(new CType("unsigned char", 0), Detail(scan, "byte").Type);
        Assert.Equal(new CType("sprite_t", 1), Detail(scan, "sprites").Type);
        Assert.Equal(new CType("sprite_t", 1), Detail(scan, "current").Type);
        Assert.Equal(new CType("char", 2), Detail(scan, "names").Type);
        Assert.Equal(new CType("sprite_t", 1), Detail(scan, "s").Type);
        Assert.Equal(new CType("unsigned char", 0), Detail(scan, "count").Type);
        Assert.Equal(new CType("struct point", 0), Detail(scan, "corner").Type);
        Assert.Equal(new CType("struct point", 1), Detail(scan, "edge").Type);
        Assert.Equal(new CType("struct player", 1), Detail(scan, "player_ptr").Type);
    }

    [Fact]
    public void Members_KnowTheirStruct_EvenWithoutATag()
    {
        var scan = Scan();

        Assert.Equal("struct point", Detail(scan, "x").Container);
        var sprite = Detail(scan, "sprite_t").Type!;
        Assert.True(sprite.IsAggregate);
        Assert.Equal(sprite.Base, Detail(scan, "pos").Container);
        Assert.Equal(new CType("struct point", 0), Detail(scan, "pos").Type);
        Assert.Equal(new CType("byte", 2), Detail(scan, "frames").Type);  // an array of pointers
    }

    [Fact]
    public void Functions_HaveReturnTypesAndSignatures()
    {
        var scan = Scan();

        var find = Detail(scan, "find_player");
        Assert.Equal(new CType("struct player", 1), find.Type);
        Assert.Equal("struct player *find_player(byte id, const char *name)", find.Signature!.Text);
        Assert.Equal(["byte id", "const char *name"], find.Signature.Parameters.Select(p => find.Signature.Text.Substring(p.Start, p.Length)));

        var draw = Detail(scan, "draw", SymbolKind.Function).Signature!;
        Assert.Equal("void draw(sprite_t *s, unsigned char count)", draw.Text);

        var clamp = Detail(scan, "CLAMP").Signature!;
        Assert.Equal("CLAMP(v, lo, hi)", clamp.Text);
        Assert.Equal(3, clamp.Parameters.Count);
    }

    [Fact]
    public void AVoidParameterList_HasNoParameters()
    {
        var scan = CSymbolScanner.ScanDetailed("t.c", SourceTokenizer.TokenizeC("int tick(void);\n"));

        Assert.Empty(Detail(scan, "tick").Signature!.Parameters);
    }
}
