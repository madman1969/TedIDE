using System.Xml;
using Terminal.Gui.Editor.Highlighting;
using Terminal.Gui.Editor.Highlighting.Xshd;

namespace Tedide.App.Highlighting;

/// <summary>
/// Registers a hand-written syntax highlighting definition for ld65 linker configuration files
/// (.cfg - see <see cref="Tedide.Core.TedideProject.LinkerConfigPath"/>) with Terminal.Gui.Editor's
/// <see cref="HighlightingManager"/>, the same way <see cref="Cc65LinkerMapHighlighting"/> does for
/// lnk.map files.
///
/// A .cfg file is a small block-structured grammar (see the ld65 manual's "Configuration file"
/// section, bundled in Tedide.DocViewer's cc65 docs): top-level blocks like
///   MEMORY { NAME: start = $0801, size = $9800, type = rw, file = %O, define = yes; ... }
///   SEGMENTS { NAME: load = AREA, run = AREA, type = ro, define = yes; ... }
/// (also SYMBOLS/FEATURES/FILES/FORMATS blocks), each statement comma-separated and semicolon-
/// terminated, values either hex literals ($...), the %O/%L/%N output-file placeholders, quoted
/// strings, or one of a small set of attribute-value keywords (ro/rw/bss/yes/no/...). Comments run
/// from # to end of line. This is presentational only, in the same spirit as
/// <see cref="Cc65LinkerMapHighlighting"/>'s own heuristics - not a real parser.
/// </summary>
public static class Cc65CfgHighlighting
{
    private const string DefinitionXml = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="cc65 Linker Config" extensions=".cfg" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
        	<Color name="BlockKeyword" foreground="DarkCyan" fontWeight="bold" />
        	<Color name="AttributeName" foreground="#FF0000FF" fontWeight="bold" />
        	<Color name="AttributeValue" foreground="#FF800080" />
        	<Color name="Placeholder" foreground="#FF008B8B" fontWeight="bold" />
        	<Color name="Number" foreground="DarkBlue" />
        	<Color name="String" foreground="Green" />
        	<Color name="Comment" foreground="Gray" fontStyle="italic" />
        	<Color name="AreaName" foreground="Maroon" fontWeight="bold" />
        	<RuleSet ignoreCase="false">
        		<Span color="Comment" begin="#" />
        		<Span color="String" begin="&quot;" end="&quot;" />
        		<Rule color="Number">\$[0-9A-Fa-f]+</Rule>
        		<Rule color="Placeholder">%[OLN]</Rule>
        		<Rule color="AreaName">[A-Za-z_][A-Za-z0-9_]*\s*:</Rule>
        		<Keywords color="BlockKeyword">
        			<Word>MEMORY</Word><Word>SEGMENTS</Word><Word>SYMBOLS</Word>
        			<Word>FEATURES</Word><Word>FILES</Word><Word>FORMATS</Word>
        		</Keywords>
        		<Keywords color="AttributeName">
        			<Word>start</Word><Word>size</Word><Word>type</Word><Word>file</Word>
        			<Word>define</Word><Word>load</Word><Word>run</Word><Word>align</Word>
        			<Word>offset</Word><Word>optional</Word><Word>fill</Word><Word>fillval</Word>
        			<Word>export</Word><Word>import</Word><Word>condes</Word><Word>bank</Word>
        			<Word>banksize</Word><Word>name</Word>
        		</Keywords>
        		<Keywords color="AttributeValue">
        			<Word>ro</Word><Word>rw</Word><Word>bss</Word><Word>overwrite</Word>
        			<Word>yes</Word><Word>no</Word><Word>weak</Word><Word>import</Word><Word>expr</Word>
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
        HighlightingManager.Instance.RegisterHighlighting(definition.Name, [".cfg"], definition);
    }
}
