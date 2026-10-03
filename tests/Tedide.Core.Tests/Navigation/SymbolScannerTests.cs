using Tedide.Core.Navigation;

namespace Tedide.Core.Tests.Navigation;

public class SymbolScannerTests
{
    private static List<SymbolDefinition> ScanC(string text) =>
        CSymbolScanner.Scan("t.c", SourceTokenizer.TokenizeC(text));

    private static List<SymbolDefinition> ScanAsm(string text) =>
        AsmSymbolScanner.Scan("t.s", SourceTokenizer.TokenizeAssembly(text));

    private static SymbolKind KindOf(List<SymbolDefinition> definitions, string name) =>
        Assert.Single(definitions, d => d.Name == name).Kind;

    [Fact]
    public void TokenizeC_DropsCommentsAndKeepsStringsAsSingleTokens()
    {
        var tokens = SourceTokenizer.TokenizeC("a /* b\n c */ d // e\n\"f g\" 'h' 1.5e3");

        Assert.Equal(["a", "d", "\"f g\"", "'h'", "1.5e3"], tokens.Select(t => t.Text));
        Assert.Equal(2, tokens[1].Line);
        Assert.Equal(TokenKind.Number, tokens[^1].Kind);
    }

    [Fact]
    public void TokenizeC_MarksPreprocessorLines_IncludingContinuations()
    {
        var tokens = SourceTokenizer.TokenizeC("#define A \\\n  1\nint b;\n#include <x.h>");

        Assert.All(tokens.Where(t => t.Text is "define" or "A" or "1"), t => Assert.Equal(1, t.Directive));
        Assert.Equal(0, tokens.Single(t => t.Text == "b").Directive);
        Assert.Contains(tokens, t => t is { Kind: TokenKind.String, Text: "<x.h>", Directive: 2 });
    }

    [Fact]
    public void TokenizeAssembly_ReadsHexAndBinaryNumbersCheapLocalsAndDirectives()
    {
        var tokens = SourceTokenizer.TokenizeAssembly("@x: lda #$FF ; comment\n .byte %0101, \"s;t\"");

        Assert.Equal(["@x", ":", "lda", "#", "$FF", ".byte", "%0101", ",", "\"s;t\""], tokens.Select(t => t.Text));
        Assert.Equal(TokenKind.Directive, tokens[5].Kind);
        Assert.Equal(TokenKind.Number, tokens[4].Kind);
    }

    [Fact]
    public void LanguageOf_UsesTheExtension()
    {
        Assert.Equal(SourceLanguage.C, SourceTokenizer.LanguageOf("a.H"));
        Assert.Equal(SourceLanguage.Assembly, SourceTokenizer.LanguageOf("a.inc"));
        Assert.Equal(SourceLanguage.Other, SourceTokenizer.LanguageOf("a.cfg"));
        Assert.Empty(SourceTokenizer.Tokenize("x", SourceLanguage.Other));
    }

    [Fact]
    public void ScanC_ClassifiesFileScopeDeclarations()
    {
        var definitions = ScanC("""
            #define MAX(a, b) ((a) > (b) ? (a) : (b))
            typedef unsigned char byte;
            typedef void (*handler)(void);
            struct node { struct node *next; int value; } head, *tail;
            enum colour { RED, GREEN = 5, BLUE };
            extern int counter;
            int table[4] = { 1, 2, 3, 4 }, other;
            int add(int x, int y);
            static void (*callback)(int);
            struct node;
            """);

        Assert.Equal(SymbolKind.Macro, KindOf(definitions, "MAX"));
        Assert.All(definitions.Where(d => d.Name is "a" or "b"), d => Assert.Equal(SymbolKind.Parameter, d.Kind));
        Assert.Equal(SymbolKind.Typedef, KindOf(definitions, "byte"));
        Assert.Equal(SymbolKind.Typedef, KindOf(definitions, "handler"));
        Assert.Equal(SymbolKind.Tag, KindOf(definitions, "node"));
        Assert.Equal(SymbolKind.Member, KindOf(definitions, "next"));
        Assert.Equal(SymbolKind.Member, KindOf(definitions, "value"));
        Assert.Equal(SymbolKind.Variable, KindOf(definitions, "head"));
        Assert.Equal(SymbolKind.Variable, KindOf(definitions, "tail"));
        Assert.Equal(SymbolKind.Tag, KindOf(definitions, "colour"));
        Assert.All(definitions.Where(d => d.Name is "RED" or "GREEN" or "BLUE"), d => Assert.Equal(SymbolKind.EnumConstant, d.Kind));
        Assert.Equal(3, definitions.Count(d => d.Kind == SymbolKind.EnumConstant));
        Assert.Equal(SymbolKind.ExternVariable, KindOf(definitions, "counter"));
        Assert.Equal(SymbolKind.Variable, KindOf(definitions, "table"));
        Assert.Equal(SymbolKind.Variable, KindOf(definitions, "other"));
        Assert.Equal(SymbolKind.Prototype, KindOf(definitions, "add"));
        Assert.Equal(SymbolKind.Variable, KindOf(definitions, "callback"));
        // A prototype's parameter names are visible only within the prototype itself.
        Assert.All(definitions.Where(d => d.Name is "x" or "y"), d => Assert.Equal(new SourceScope(8, 8), d.Scope));
        // "struct node;" is only a forward declaration: just the one tag, from the definition.
        Assert.Single(definitions, d => d.Name == "node");
    }

    [Fact]
    public void ScanC_ScopesParametersAndLocalsToTheirBlocks()
    {
        var definitions = ScanC("""
            int f(int n, char *s)
            {
                int total = 0;
                byte b;
                byte *p = 0;
                total = n * 2;
                if (n) {
                    int inner;
                }
                return total;
            }
            """);

        Assert.Equal(SymbolKind.Function, KindOf(definitions, "f"));
        Assert.Equal(new SourceScope(1, 11), Assert.Single(definitions, d => d.Name == "n").Scope);
        Assert.Equal(SymbolKind.Parameter, KindOf(definitions, "s"));
        Assert.Equal(new SourceScope(3, 11), Assert.Single(definitions, d => d.Name == "total").Scope);
        Assert.Equal(SymbolKind.LocalVariable, KindOf(definitions, "b"));
        Assert.Equal(SymbolKind.LocalVariable, KindOf(definitions, "p"));
        Assert.Equal(new SourceScope(8, 9), Assert.Single(definitions, d => d.Name == "inner").Scope);
        // "total = n * 2;" is a statement, not a declaration of anything.
        Assert.Single(definitions, d => d.Name == "total");
    }

    [Fact]
    public void ScanC_SurvivesUnbalancedBraces()
    {
        var definitions = ScanC("void f(int a)\n{\n  int b;\n");

        Assert.Equal(new SourceScope(3, 3), Assert.Single(definitions, d => d.Name == "b").Scope);
    }

    [Fact]
    public void ScanAsm_ClassifiesDefinitions()
    {
        var definitions = ScanAsm("""
                    .import _a, _b
                    .importzp sp
                    .export _done = 1
            SCREEN  = $0400
            COLOR   := $d800
            count   .set 0
            .proc   _main: near
            @x:     rts
            .endproc
            .macro  poke addr, value
                    lda #value
                    sta addr
            .endmacro
            .define WIDTH 40
            .struct point
                    xpos .byte
                    ypos .byte
            .endstruct
            .enum   mode
                    TEXT
                    BITMAP = 2
            .endenum
            .scope  inner
            .endscope
            done:   rts
            """);

        Assert.All(definitions.Where(d => d.Name is "_a" or "_b" or "sp"), d => Assert.Equal(SymbolKind.Import, d.Kind));
        Assert.Equal(SymbolKind.Constant, KindOf(definitions, "_done"));
        Assert.Equal(SymbolKind.Constant, KindOf(definitions, "SCREEN"));
        Assert.Equal(SymbolKind.Constant, KindOf(definitions, "COLOR"));
        Assert.Equal(SymbolKind.Constant, KindOf(definitions, "count"));
        Assert.Equal(SymbolKind.Function, KindOf(definitions, "_main"));
        // From .proc to just before "done:", the next ordinary label.
        Assert.Equal(new SourceScope(7, 24), Assert.Single(definitions, d => d.Name == "@x").Scope);
        Assert.Equal(SymbolKind.Macro, KindOf(definitions, "poke"));
        Assert.Equal(new SourceScope(10, 13), Assert.Single(definitions, d => d.Name == "addr" && d.Line == 10).Scope);
        Assert.Equal(SymbolKind.Macro, KindOf(definitions, "WIDTH"));
        Assert.Equal(SymbolKind.Tag, KindOf(definitions, "point"));
        Assert.Equal(SymbolKind.Member, KindOf(definitions, "xpos"));
        Assert.Equal(SymbolKind.Tag, KindOf(definitions, "mode"));
        Assert.Equal(SymbolKind.EnumConstant, KindOf(definitions, "TEXT"));
        Assert.Equal(SymbolKind.EnumConstant, KindOf(definitions, "BITMAP"));
        Assert.Equal(SymbolKind.Label, KindOf(definitions, "inner"));
        Assert.Equal(SymbolKind.Label, KindOf(definitions, "done"));
    }

    [Fact]
    public void ScanAsm_ClosesAnUnterminatedMacroAtTheEndOfTheFile()
    {
        var definitions = ScanAsm(".macro m arg\n lda arg\n");

        Assert.Equal(new SourceScope(1, 2), Assert.Single(definitions, d => d.Name == "arg").Scope);
    }

    [Fact]
    public void SymbolDefinition_DescribesItsKind()
    {
        Assert.All(Enum.GetValues<SymbolKind>(), kind =>
            Assert.False(string.IsNullOrEmpty(new SymbolDefinition("x", kind, "f", 1, 1).KindText)));
        Assert.True(new SymbolDefinition("x", SymbolKind.Import, "f", 1, 1).IsDeclaration);
        Assert.False(new SymbolDefinition("x", SymbolKind.Label, "f", 1, 1).IsDeclaration);
    }
}
