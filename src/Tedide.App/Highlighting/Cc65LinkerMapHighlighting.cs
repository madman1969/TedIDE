using System.Xml;
using Terminal.Gui.Editor.Highlighting;
using Terminal.Gui.Editor.Highlighting.Xshd;

namespace Tedide.App.Highlighting;

/// <summary>
/// Registers a hand-written syntax highlighting definition for ld65's linker map (lnk.map - see
/// <see cref="Tedide.Core.TedideProject.ResolvedMapFile"/>) with Terminal.Gui.Editor's
/// <see cref="HighlightingManager"/>, the same way <see cref="Cc65ListingHighlighting"/> does for
/// .lst files.
///
/// A map file is a fixed sequence of sections (see samples/HelloC64/lnk.map, once built with the
/// Linker tab's "Generate linker map file" on, for a worked example):
///   Modules list:    - one "module.o:" (or "archive.lib(module.o):") header line per input,
///                     each followed by that module's segments as "    NAME  Offs=.. Size=..
///                     Align=.. Fill=.." lines
///   Segment list:     - a table of every output segment's Start/End/Size/Align
///   Exports list ...  - two name/address/flag columns per line (by name, then again by address)
///   Imports list:     - one "symbol (definingmodule.o):" header line per imported symbol, each
///                     followed by the modules that reference it as "    module.o  file.s:line"
/// The section/table headers and their "----" underlines, the module/import name lines (anything
/// starting at column 0 and ending in ":" - column 0 is exactly what distinguishes them from the
/// indented segment/reference lines under them), hex addresses and the Offs=/Size=/Align=/Fill=
/// field names, cc65's fixed set of segment names, and the RLA/RLZ/REA-style export/import flags
/// each get their own rule below - approximate heuristics in the same spirit as
/// <see cref="Cc65ListingHighlighting"/>'s own address-prefix detection, not a real parser.
/// </summary>
public static class Cc65LinkerMapHighlighting
{
    private const string DefinitionXml = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="cc65 Linker Map" extensions=".map" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
        	<Color name="SectionHeader" foreground="DarkCyan" fontWeight="bold" />
        	<Color name="Separator" foreground="Gray" />
        	<Color name="ModuleName" foreground="#FF800080" fontWeight="bold" />
        	<Color name="Field" foreground="#FF008B8B" fontWeight="bold" />
        	<Color name="SegmentName" foreground="#FF0000FF" fontWeight="bold" />
        	<Color name="Address" foreground="DarkBlue" />
        	<Color name="Flag" foreground="Maroon" />
        	<Color name="Path" foreground="Green" />
        	<Color name="LinkerGenerated" foreground="Gray" />
        	<RuleSet ignoreCase="false">
        		<Rule color="SectionHeader">^(Modules\ list:|Segment\ list:|Exports\ list\ by\ name:|Exports\ list\ by\ value:|Imports\ list:|Name\ +Start\ +End\ +Size\ +Align)$</Rule>
        		<Rule color="Separator">^-+$</Rule>
        		<Rule color="ModuleName">^\S.*:$</Rule>
        		<Rule color="LinkerGenerated">\[linker\ generated\]</Rule>
        		<Rule color="Path">[A-Za-z0-9_./\\]+\.[A-Za-z]+:[0-9]+</Rule>
        		<Rule color="Field">\b(Offs|Size|Align|Fill)=</Rule>
        		<Rule color="Address">\b[0-9A-Fa-f]{6}\b</Rule>
        		<Rule color="Flag">\b[A-Z]{3}\b</Rule>
        		<Keywords color="SegmentName">
        			<Word>STARTUP</Word><Word>LOWCODE</Word><Word>INIT</Word><Word>CODE</Word>
        			<Word>RODATA</Word><Word>DATA</Word><Word>BSS</Word><Word>ZEROPAGE</Word>
        			<Word>EXTZP</Word><Word>EXEHDR</Word><Word>LOADADDR</Word><Word>ONCE</Word>
        			<Word>EXTRA</Word>
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
        HighlightingManager.Instance.RegisterHighlighting(definition.Name, [".map"], definition);
    }
}
