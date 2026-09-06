using System.Xml;
using Terminal.Gui.Editor.Highlighting;
using Terminal.Gui.Editor.Highlighting.Xshd;

namespace Tedide.App.Highlighting;

/// <summary>
/// Registers a hand-written syntax highlighting definition for 6502/ca65 assembly (.s/.asm) with
/// Terminal.Gui.Editor's <see cref="HighlightingManager"/> - it ships definitions for C/C++, C#,
/// JS, HTML, CSS, Go, Java, PowerShell, Python, SQL, VB, XML, Markdown and JSON, but nothing for
/// 6502 assembly, so without this <see cref="Editor.HighlightingDefinition"/> is always null for
/// a .s/.asm file and it renders as plain, uncolored text.
///
/// Uses the same XSHD (AvalonEdit/SharpDevelop) XML format the bundled definitions are authored
/// in - see <see cref="HighlightingLoader"/> - covering: line comments (";"), string/character
/// literals, ca65 directives (".proc", ".export", etc. - matched generically by a leading "."
/// rather than an exhaustive list, since ca65 has 100+ pseudo-ops), labels ("name:"), hex/binary/
/// decimal numeric literals ("$d020", "%1010", "10"), the 56 official 6502 mnemonics, and common
/// assembly punctuation. Like the bundled definitions, colors are literal values baked into the
/// XML rather than resolved through Tedide's own Scheme-based themes (see ThemeSwitcher) - so
/// switching app themes restyles the editor's background/chrome but not these token colors,
/// consistent with how the bundled C highlighting already behaves.
/// </summary>
public static class Cc65AssemblyHighlighting
{
    private const string DefinitionXml = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="6502 Assembly" extensions=".s;.asm" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
        	<Color name="Comment" foreground="Green" />
        	<Color name="String" foreground="Fuchsia" />
        	<Color name="Character" foreground="Fuchsia" />
        	<Color name="Mnemonic" foreground="#FF0000FF" fontWeight="bold" />
        	<Color name="Directive" foreground="#FF008B8B" fontWeight="bold" />
        	<Color name="Label" foreground="#FF800080" fontWeight="bold" />
        	<Color name="Number" foreground="DarkBlue" />
        	<Color name="Punctuation" foreground="DarkGreen" />
        	<RuleSet ignoreCase="true">
        		<Span color="Comment">
        			<Begin>;</Begin>
        		</Span>
        		<Span color="String">
        			<Begin>"</Begin>
        			<End>"</End>
        		</Span>
        		<Span color="Character">
        			<Begin>'</Begin>
        			<End>'</End>
        		</Span>
        		<Rule color="Label">\b[A-Za-z_@][A-Za-z0-9_]*:</Rule>
        		<Rule color="Directive">\.[A-Za-z_][A-Za-z0-9_]*</Rule>
        		<Rule color="Number">\$[0-9A-Fa-f]+|%[01]+|\b[0-9]+\b</Rule>
        		<Rule color="Punctuation">[#,()\[\]:+\-*/=~&lt;&gt;!&amp;|^]+</Rule>
        		<Keywords color="Mnemonic">
        			<Word>adc</Word><Word>and</Word><Word>asl</Word><Word>bcc</Word><Word>bcs</Word>
        			<Word>beq</Word><Word>bit</Word><Word>bmi</Word><Word>bne</Word><Word>bpl</Word>
        			<Word>brk</Word><Word>bvc</Word><Word>bvs</Word><Word>clc</Word><Word>cld</Word>
        			<Word>cli</Word><Word>clv</Word><Word>cmp</Word><Word>cpx</Word><Word>cpy</Word>
        			<Word>dec</Word><Word>dex</Word><Word>dey</Word><Word>eor</Word><Word>inc</Word>
        			<Word>inx</Word><Word>iny</Word><Word>jmp</Word><Word>jsr</Word><Word>lda</Word>
        			<Word>ldx</Word><Word>ldy</Word><Word>lsr</Word><Word>nop</Word><Word>ora</Word>
        			<Word>pha</Word><Word>php</Word><Word>pla</Word><Word>plp</Word><Word>rol</Word>
        			<Word>ror</Word><Word>rti</Word><Word>rts</Word><Word>sbc</Word><Word>sec</Word>
        			<Word>sed</Word><Word>sei</Word><Word>sta</Word><Word>stx</Word><Word>sty</Word>
        			<Word>tax</Word><Word>tay</Word><Word>tsx</Word><Word>txa</Word><Word>txs</Word>
        			<Word>tya</Word>
        		</Keywords>
        	</RuleSet>
        </SyntaxDefinition>
        """;

    /// <summary>Idempotent - safe to call more than once (later registrations just replace earlier ones).</summary>
    public static void Register()
    {
        using var stringReader = new StringReader(DefinitionXml);
        using var xmlReader = XmlReader.Create(stringReader);
        var definition = HighlightingLoader.Load(xmlReader, HighlightingManager.Instance);
        HighlightingManager.Instance.RegisterHighlighting(definition.Name, [".s", ".asm"], definition);
    }
}
