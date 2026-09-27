using Tedide.App.Highlighting;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Highlighting;

namespace Tedide.App.Tests;

/// <summary>
/// Runs Tedide's own syntax definitions over lines of real cc65 output and checks which named
/// color each piece gets - the definitions are regex rules in XML, so the only way to know they
/// color what they should is to run them.
/// </summary>
public class HighlightingTests
{
    static HighlightingTests()
    {
        Cc65AssemblyHighlighting.Register();
        Cc65ListingHighlighting.Register();
        Cc65LinkerMapHighlighting.Register();
        Cc65LabelsHighlighting.Register();
        Cc65CfgHighlighting.Register();
    }

    [Theory]
    // Cheap local labels: the "@" used to be left uncolored, and uses of one not colored at all.
    [InlineData(".s", "@loop:  dex", "Label:@loop:", "Mnemonic:dex")]
    [InlineData(".s", "        bne     @loop", "Mnemonic:bne", "Label:@loop")]
    [InlineData(".s", "L0002:  jsr     _animation_step", "Label:L0002:", "Mnemonic:jsr")]
    // 65C02 additions, for cc65's 65C02 targets.
    [InlineData(".s", "        stz     $D020", "Mnemonic:stz", "Number:$D020")]
    [InlineData(".s", "        bra     @done", "Mnemonic:bra", "Label:@done")]
    // A ";" inside a string is not a comment.
    [InlineData(".s", "        .byte   \"semi;colon\" ; real comment", "Directive:.byte", "String:\"semi;colon\"", "Comment:; real comment")]
    [InlineData(".lst", "000000r 1  20 rr rr     	jsr     _screen_init", "Address:000000r 1  20 rr rr ", "Mnemonic:jsr")]
    [InlineData(".lst", "000010r 1  80 FE        @wait:  bra     @wait", "Address:000010r 1  80 FE ", "Label:@wait:", "Mnemonic:bra", "Label:@wait")]
    // Every value after a map field, not just the 6-digit ones (Align/Fill are shorter).
    [InlineData(".map", "    CODE              Offs=000000  Size=000016  Align=00001  Fill=0000",
        "SegmentName:CODE", "Field:Offs=", "Address:000000", "Field:Size=", "Address:000016", "Field:Align=", "Address:00001", "Field:Fill=", "Address:0000")]
    [InlineData(".map", "main.c.o:", "ModuleName:main.c.o:")]
    [InlineData(".lbl", "al 000710 .BSOUT", "Command:al", "Address:000710", "Symbol:.BSOUT")]
    // "default" (STARTADDRESS) used to be the one attribute name left uncolored.
    [InlineData(".cfg", "    STARTADDRESS: default = $0801;", "AreaName:STARTADDRESS:", "AttributeName:default", "Number:$0801")]
    [InlineData(".cfg", "    __STACKSIZE__: type = weak, value = $0800; # 2k stack",
        "AreaName:__STACKSIZE__:", "AttributeName:type", "AttributeValue:weak", "AttributeName:value", "Number:$0800", "Comment:# 2k stack")]
    [InlineData(".cfg", "    CONDES: type = constructor, label = __CONSTRUCTOR_TABLE__, segment = ONCE;",
        "AreaName:CONDES:", "AttributeName:type", "AttributeValue:constructor", "AttributeName:label", "AttributeName:segment")]
    public void Line_IsColoredAsExpected(string extension, string line, params string[] expected)
    {
        Assert.Equal(expected, Highlight(extension, line));
    }

    [Theory]
    [InlineData(".s")]
    [InlineData(".lst")]
    [InlineData(".map")]
    [InlineData(".lbl")]
    [InlineData(".cfg")]
    public void EveryColor_MapsToAThemeRole(string extension)
    {
        // A color without a role is drawn in its fixed xshd color on every theme - see
        // ThemeSwitcher.BuildScheme.
        var unmapped = HighlightingManager.Instance.GetDefinitionByExtension(extension)!.NamedHighlightingColors
            .Where(c => c.Role is null).Select(c => c.Name);

        Assert.Empty(unmapped);
    }

    [Theory]
    [InlineData(".s")]
    [InlineData(".asm")]
    [InlineData(".lst")]
    [InlineData(".map")]
    [InlineData(".lbl")]
    [InlineData(".cfg")]
    public void EveryCc65Extension_HasADefinition(string extension)
    {
        Assert.NotNull(HighlightingManager.Instance.GetDefinitionByExtension(extension));
    }

    private static string[] Highlight(string extension, string line)
    {
        var definition = HighlightingManager.Instance.GetDefinitionByExtension(extension)!;
        var document = new TextDocument(line);
        var highlighted = new DocumentHighlighter(document, definition).HighlightLine(1);
        return highlighted.Sections.Select(s => $"{s.Color.Name}:{document.GetText(s.Offset, s.Length)}").ToArray();
    }
}
