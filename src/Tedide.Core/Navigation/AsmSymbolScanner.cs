namespace Tedide.Core.Navigation;

/// <summary>
/// Finds the symbols a ca65 file defines or declares: labels (cheap <c>@locals</c> scoped between
/// the ordinary labels around them), <c>name = value</c> constants, <c>.proc</c>, <c>.macro</c> and
/// <c>.define</c>, <c>.struct</c>/<c>.union</c>/<c>.enum</c> with their members, <c>.scope</c>, and
/// <c>.import</c>/<c>.global</c> declarations. <see cref="ScanDetailed"/> also records each
/// <c>.proc</c>, <c>.scope</c>, <c>.struct</c>, <c>.union</c>, <c>.enum</c> and <c>.macro</c> block's
/// extent (<see cref="SymbolDetail.Body"/>), for the Document Outline.
/// </summary>
public static class AsmSymbolScanner
{
    public static List<SymbolDefinition> Scan(string path, IReadOnlyList<SourceToken> tokens) =>
        ScanDetailed(path, tokens).Definitions;

    /// <summary>The block directives with an end, by what ends them - see <see cref="ScanDetailed"/>.</summary>
    private static readonly Dictionary<string, string> BlockEnds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".endproc"] = ".proc", [".endscope"] = ".scope", [".endstruct"] = ".struct", [".endunion"] = ".union",
        [".endenum"] = ".enum", [".endmacro"] = ".macro", [".endmac"] = ".macro",
    };

    public static CScan ScanDetailed(string path, IReadOnlyList<SourceToken> tokens)
    {
        var definitions = new List<SymbolDefinition>();
        var details = new Dictionary<SymbolDefinition, SymbolDetail>();
        // Open blocks: the directive that opened each (".mac" counted as ".macro"), where, and its name.
        var blocks = new Stack<(string Directive, int Line, SymbolDefinition? Name)>();
        var cheapLocals = new List<SourceToken>();
        // Every line that starts a new cheap-local scope: an ordinary label or a .proc.
        var scopeBreaks = new List<int>();
        var structDepth = 0;
        var enumDepth = 0;
        (int StartLine, List<SourceToken> Parameters)? macro = null;

        SymbolDefinition Add(SourceToken token, SymbolKind kind, SourceScope? scope = null)
        {
            var definition = new SymbolDefinition(token.Text, kind, path, token.Line, token.Column, scope);
            definitions.Add(definition);
            return definition;
        }

        void Open(string directive, int line, SymbolDefinition? name)
        {
            blocks.Push((directive, line, name));
            if (name is not null)
                details[name] = new SymbolDetail(Keyword: directive);
        }

        // Closes the innermost block the end directive matches, and any left open inside it.
        void Close(string opener, int line)
        {
            if (!blocks.Any(b => b.Directive == opener))
                return;
            while (blocks.TryPop(out var block))
            {
                if (block.Name is { } name)
                    details[name] = details[name] with { Body = new SourceScope(block.Line, line) };
                if (block.Directive == opener)
                    break;
            }
        }

        static bool IsName(List<SourceToken> line, int index) =>
            index < line.Count && line[index].Kind == TokenKind.Identifier;

        foreach (var line in tokens.GroupBy(t => t.Line).Select(g => g.ToList()))
        {
            var i = 0;

            // "name:" - but not "name := value", which is an assignment.
            if (IsName(line, 0) && line.Count >= 2 && line[1].Is(":")
                && !(line.Count >= 3 && line[2].Is("=") && line[2].Column == line[1].EndColumn))
            {
                if (line[0].Text.StartsWith('@'))
                {
                    cheapLocals.Add(line[0]);
                }
                else
                {
                    Add(line[0], SymbolKind.Label);
                    scopeBreaks.Add(line[0].Line);
                }
                i = line.Count >= 3 && line[2].Is(":") ? 3 : 2;
            }

            if (i >= line.Count)
                continue;

            // An .enum member can have a value ("BITMAP = 2") without being a constant of its own.
            if (IsName(line, i) && enumDepth > 0 && structDepth == 0)
            {
                Add(line[i], SymbolKind.EnumConstant);
                continue;
            }

            // "name = value", "name := value", "name .set value".
            if (IsName(line, i) && i + 1 < line.Count
                && (line[i + 1].Is("=") || line[i + 1].Text.Equals(".set", StringComparison.OrdinalIgnoreCase)
                    || (line[i + 1].Is(":") && i + 2 < line.Count && line[i + 2].Is("="))))
            {
                Add(line[i], SymbolKind.Constant);
                continue;
            }

            if (line[i].Kind != TokenKind.Directive)
            {
                // Inside a .struct body, a line that starts with a name defines a member.
                if (IsName(line, i) && structDepth > 0)
                    Add(line[i], SymbolKind.Member);
                continue;
            }

            var directive = line[i].Text.ToLowerInvariant();
            if (BlockEnds.TryGetValue(directive, out var opener))
                Close(opener, line[i].Line);

            switch (directive)
            {
                case ".proc":
                    Open(".proc", line[i].Line, IsName(line, i + 1) ? Add(line[i + 1], SymbolKind.Function) : null);
                    if (IsName(line, i + 1))
                        scopeBreaks.Add(line[i].Line);
                    break;
                case ".macro" or ".mac":
                    Open(".macro", line[i].Line, IsName(line, i + 1) ? Add(line[i + 1], SymbolKind.Macro) : null);
                    if (IsName(line, i + 1))
                        macro = (line[i].Line, line.Skip(i + 2).Where(t => t.Kind == TokenKind.Identifier).ToList());
                    break;
                case ".endmacro" or ".endmac" when macro is { } open:
                    foreach (var parameter in open.Parameters)
                        Add(parameter, SymbolKind.Parameter, new SourceScope(open.StartLine, line[i].Line));
                    macro = null;
                    break;
                case ".define" when IsName(line, i + 1):
                    Add(line[i + 1], SymbolKind.Macro);
                    break;
                case ".struct" or ".union":
                    Open(directive, line[i].Line, IsName(line, i + 1) ? Add(line[i + 1], SymbolKind.Tag) : null);
                    structDepth++;
                    break;
                case ".endstruct" or ".endunion":
                    structDepth = Math.Max(0, structDepth - 1);
                    break;
                case ".enum":
                    Open(".enum", line[i].Line, IsName(line, i + 1) ? Add(line[i + 1], SymbolKind.Tag) : null);
                    enumDepth++;
                    break;
                case ".endenum":
                    enumDepth = Math.Max(0, enumDepth - 1);
                    break;
                case ".scope":
                    Open(".scope", line[i].Line, IsName(line, i + 1) ? Add(line[i + 1], SymbolKind.Label) : null);
                    break;
                case ".import" or ".importzp" or ".global" or ".globalzp" or ".forceimport":
                    foreach (var name in line.Skip(i + 1).Where(t => t.Kind == TokenKind.Identifier))
                        Add(name, SymbolKind.Import);
                    break;
                case ".export" or ".exportzp" when IsName(line, i + 1) && i + 2 < line.Count
                    && (line[i + 2].Is("=") || (line[i + 2].Is(":") && i + 3 < line.Count && line[i + 3].Is("="))):
                    // ".export name = value" defines the constant as well as exporting it.
                    Add(line[i + 1], SymbolKind.Constant);
                    break;
            }
        }

        // A cheap local lives from the ordinary label before it to just before the next one.
        var lastLine = tokens.Count > 0 ? tokens[^1].Line : 1;
        foreach (var local in cheapLocals)
        {
            var start = scopeBreaks.LastOrDefault(l => l <= local.Line, 1);
            var next = scopeBreaks.FirstOrDefault(l => l > local.Line, lastLine + 1);
            Add(local, SymbolKind.Label, new SourceScope(start, next - 1));
        }

        if (macro is { } unclosed)
            foreach (var parameter in unclosed.Parameters)
                Add(parameter, SymbolKind.Parameter, new SourceScope(unclosed.StartLine, lastLine));

        // Half-typed code: a block with no end runs to the end of the file.
        foreach (var (_, line, name) in blocks)
            if (name is not null)
                details[name] = details[name] with { Body = new SourceScope(line, lastLine) };

        return new CScan(definitions, details);
    }
}
