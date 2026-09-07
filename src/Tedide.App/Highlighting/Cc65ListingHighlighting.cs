using System.Xml;
using Terminal.Gui.Editor.Highlighting;
using Terminal.Gui.Editor.Highlighting.Xshd;

namespace Tedide.App.Highlighting;

/// <summary>
/// Registers a hand-written syntax highlighting definition for cc65 assembler listings (.lst -
/// see <see cref="Tedide.Core.TedideProject.ResolvedListingFiles"/>) with Terminal.Gui.Editor's
/// <see cref="HighlightingManager"/>, the same way <see cref="Cc65AssemblyHighlighting"/> does for
/// .s/.asm.
///
/// A listing line has two parts: a fixed-shape ca65-generated prefix (a 4-6 digit hex address,
/// optionally suffixed with "r" for relocatable, then an include/macro nesting-depth number, then
/// zero or more assembled bytes as hex pairs - or the literal placeholder "rr" where a byte's
/// value depends on a symbol ld65 hasn't resolved yet, since this file is written by ca65 before
/// linking), followed by a tab and the original source line it assembled from - see
/// samples/HelloCBM/src/border.lst (once built) for a worked example. That source-line part is
/// exactly what <see cref="Cc65AssemblyHighlighting"/> already knows how to color (including a
/// project built with AddSourceAsComment on, which turns interleaved C source lines into ordinary
/// ";"-comments ca65 was never told apart from any other comment) - its rules are duplicated here
/// (rather than shared via a common constant) since cc65's fixed, unchanging 6502 instruction set
/// makes that duplication low-risk, and Tedide's own conventions prefer that over a premature
/// shared abstraction for two consumers.
///
/// The address/byte-dump prefix is its own "Address" rule, anchored to the start of the line so it
/// can't misfire mid-line: it matches the address and nesting number unconditionally, then greedily
/// consumes hex-pair-or-"rr" tokens for as long as they keep appearing - which turns out to handle
/// every real shape correctly without needing to locate the tab that (sometimes) follows: a
/// comment-only or label-only line has no bytes to begin with, so the greedy part simply matches
/// zero of them, while a short label that manages to fit in the byte-dump's column width (e.g.
/// "L0008:" in border.lst) naturally stops the match, since neither "L0" nor "00" nor "08" is a
/// valid hex-pair-or-"rr" token once matched strictly left-to-right character by character.
/// </summary>
public static class Cc65ListingHighlighting
{
    private const string DefinitionXml = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="cc65 Listing" extensions=".lst" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
        	<Color name="Address" foreground="Gray" />
        	<Color name="Comment" foreground="Green" />
        	<Color name="String" foreground="Fuchsia" />
        	<Color name="Character" foreground="Fuchsia" />
        	<Color name="Mnemonic" foreground="#FF0000FF" fontWeight="bold" />
        	<Color name="Directive" foreground="#FF008B8B" fontWeight="bold" />
        	<Color name="Label" foreground="#FF800080" fontWeight="bold" />
        	<Color name="Number" foreground="DarkBlue" />
        	<Color name="Punctuation" foreground="DarkGreen" />
        	<RuleSet ignoreCase="true">
        		<Rule color="Address">^(ca65\ V.*|(Main|Current)\ file\ *:.*)$</Rule>
        		<Rule color="Address">^[0-9A-Fa-f]{4,6}[a-z]?\ +[0-9]+\ +((?:[0-9A-Fa-f]{2}|rr)\ ?)*</Rule>
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
        HighlightingManager.Instance.RegisterHighlighting(definition.Name, [".lst"], definition);
    }
}
