using System.Xml;
using Terminal.Gui.Editor.Highlighting;
using Terminal.Gui.Editor.Highlighting.Xshd;

namespace Tedide.App.Highlighting;

/// <summary>
/// Registers a hand-written syntax highlighting definition for ld65's VICE-format label file
/// (.lbl - see <see cref="Tedide.Core.TedideProject.ResolvedLabelsFile"/>) with Terminal.Gui.Editor's
/// <see cref="HighlightingManager"/>, the same way <see cref="Cc65LinkerMapHighlighting"/> does for
/// .map files.
///
/// Unlike the map file, a label file's shape is a single repeating line pattern - every line is a
/// VICE monitor "add label" command: the literal command "al", a 6-digit hex address, and the
/// symbol name prefixed with "." (VICE's own monitor syntax for referring to a label), e.g.
/// "al 00FFD2 .BSOUT" or "al 0008C0 ._animation_step" (see samples/HelloCBM/HelloCBM.lbl, once
/// built with the Linker tab's "Export labels" on, for a worked example - or generate one from any
/// bundled sample project the same way). No other command letter has been observed in ld65's own
/// output, so only "al" is recognized here.
/// </summary>
public static class Cc65LabelsHighlighting
{
    private const string DefinitionXml = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="VICE Label File" extensions=".lbl" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
        	<Color name="Command" foreground="#FF0000FF" fontWeight="bold" />
        	<Color name="Address" foreground="DarkBlue" />
        	<Color name="Symbol" foreground="#FF800080" fontWeight="bold" />
        	<RuleSet ignoreCase="false">
        		<Rule color="Command">^al\b</Rule>
        		<Rule color="Address">\b[0-9A-Fa-f]{6}\b</Rule>
        		<Rule color="Symbol">\.[A-Za-z_][A-Za-z0-9_]*</Rule>
        	</RuleSet>
        </SyntaxDefinition>
        """;

    /// <summary>Idempotent - safe to call more than once (later registrations just replace earlier ones).</summary>
    public static void Register()
    {
        using var stringReader = new StringReader(DefinitionXml);
        using var xmlReader = XmlReader.Create(stringReader);
        var definition = HighlightingLoader.Load(xmlReader, HighlightingManager.Instance);
        HighlightingManager.Instance.RegisterHighlighting(definition.Name, [".lbl"], definition);
    }
}
