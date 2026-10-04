namespace Tedide.Core.Navigation;

/// <summary>
/// One entry in a file's Document Outline: a symbol, the lines it covers, and what it contains.
/// </summary>
/// <param name="Text">What the outline shows: "draw(sprite_t *s) : void", "pos : struct point",
/// "struct point", "@loop"...</param>
/// <param name="StartLine">Where it starts - a function's or struct's first line, else its own.</param>
/// <param name="EndLine">Where it ends - its closing brace or end directive, else its own line.</param>
/// <param name="Inactive">In an <c>#if</c> branch the target rules out.</param>
public sealed record OutlineNode(SymbolDefinition Definition, string Text, int StartLine, int EndLine, bool Inactive,
    IReadOnlyList<OutlineNode> Children)
{
    public SymbolKind Kind => Definition.Kind;

    public bool Covers(int line) => line >= StartLine && line <= EndLine;
}

/// <summary>
/// A file's structure for the Document Outline, built from what <see cref="FileSymbols"/> already
/// found: each symbol nested in the one whose body holds it - members in their struct, enum constants
/// in their enum, everything in a ca65 <c>.proc</c> or <c>.scope</c>, a cheap <c>@local</c> under its
/// label - in file order. Parameters and local variables are left out, as in Visual Studio.
/// </summary>
public static class DocumentOutline
{
    public static IReadOnlyList<OutlineNode> Build(FileSymbols symbols)
    {
        var isAssembly = symbols.Language == SourceLanguage.Assembly;
        var entries = symbols.Definitions
            .Where(d => d.Kind is not (SymbolKind.Parameter or SymbolKind.LocalVariable or SymbolKind.File))
            .Select(d =>
            {
                var body = symbols.DetailOf(d)?.Body;
                return new Entry(d, symbols.DetailOf(d), body?.StartLine ?? d.Line, body?.EndLine ?? d.Line);
            })
            // The bigger of two blocks starting on the same line holds the other:
            // "typedef struct point {" is the typedef's and the struct's first line.
            .OrderBy(e => e.Start).ThenByDescending(e => e.End - e.Start).ThenBy(e => e.Definition.Column)
            .ToList();

        var roots = new List<Entry>();
        var open = new List<Entry>();
        foreach (var entry in entries)
        {
            open.RemoveAll(o => entry.Start > o.End);

            var parent = isAssembly && entry.Definition is { Kind: SymbolKind.Label, Scope: { } scope }
                // A cheap local belongs to the label (or .proc) that starts its scope.
                ? entries.FirstOrDefault(e => e.Definition.Line == scope.StartLine && e.Definition.Kind is SymbolKind.Label or SymbolKind.Function
                    && e.Definition.Scope is null)
                : null;
            parent ??= open.LastOrDefault(o => o.Covers(entry) && Accepts(o, entry, isAssembly));
            (parent?.Children ?? roots).Add(entry);

            // Another name for the same body ("typedef struct { } A, *PA;") adds nothing to nest in.
            if (IsContainer(entry, isAssembly) && !open.Any(o => o.Start == entry.Start && o.End == entry.End))
                open.Add(entry);
        }

        return roots.Select(e => ToNode(e, symbols)).ToList();
    }

    /// <summary>Whether a symbol's body can hold others in the outline: a C struct, union or enum (or
    /// a declaration with one), or any ca65 block - not a C function, whose contents are its locals.</summary>
    private static bool IsContainer(Entry entry, bool isAssembly) =>
        entry.Detail?.Body is not null && (isAssembly || entry.Definition.Kind is not (SymbolKind.Function or SymbolKind.Prototype));

    private static bool Accepts(Entry container, Entry child, bool isAssembly) =>
        isAssembly || child.Definition.Kind is SymbolKind.Member or SymbolKind.EnumConstant or SymbolKind.Tag;

    private static OutlineNode ToNode(Entry entry, FileSymbols symbols) =>
        new(entry.Definition, TextOf(entry.Definition, entry.Detail), entry.Start, entry.End, !symbols.IsActive(entry.Definition.Line),
            entry.Children.Select(c => ToNode(c, symbols)).ToList());

    /// <summary>What the outline shows for a symbol - its name, with a C function's parameters and
    /// return type, or a variable's, member's or typedef's type, as Visual Studio shows them.</summary>
    internal static string TextOf(SymbolDefinition definition, SymbolDetail? detail)
    {
        var name = definition.Name;
        switch (definition.Kind)
        {
            case SymbolKind.Function or SymbolKind.Prototype when detail?.Signature is { } signature:
            {
                var at = signature.Text.IndexOf(name + "(", StringComparison.Ordinal);
                if (at < 0)
                    return name;
                var returns = signature.Text[..at].Trim();
                return returns.Length > 0 ? $"{signature.Text[at..]} : {returns}" : signature.Text[at..];
            }
            case SymbolKind.Macro when detail?.Signature is { } signature:
                return signature.Text;
            case SymbolKind.Tag when detail?.Keyword is { } keyword && !keyword.StartsWith('.'):
                return $"{keyword} {name}";
            case SymbolKind.Variable or SymbolKind.ExternVariable or SymbolKind.Member or SymbolKind.Typedef
                when detail?.Type is { } type:
                return $"{name} : {TypeText(type)}";
            default:
                return name;
        }
    }

    /// <summary>A type as written - an untagged struct's position key shown as "{...}".</summary>
    private static string TypeText(CType type)
    {
        var baseText = type.Base.Contains('@') ? "{...}" : type.Base;
        return baseText + new string('*', type.Indirection);
    }

    /// <summary>The innermost node covering <paramref name="line"/>, or null - the one the outline
    /// selects as the caret moves.</summary>
    public static OutlineNode? At(IReadOnlyList<OutlineNode> nodes, int line)
    {
        foreach (var node in nodes)
        {
            if (!node.Covers(line))
                continue;
            return At(node.Children, line) ?? node;
        }
        return null;
    }

    /// <summary>The nodes whose text contains <paramref name="filter"/> (ignoring case), with the
    /// containers they sit in - a container that matches keeps all its children.</summary>
    public static IReadOnlyList<OutlineNode> Filter(IReadOnlyList<OutlineNode> nodes, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return nodes;
        var kept = new List<OutlineNode>();
        foreach (var node in nodes)
        {
            if (node.Text.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
                kept.Add(node);
            else if (Filter(node.Children, filter) is { Count: > 0 } children)
                kept.Add(node with { Children = children });
        }
        return kept;
    }

    /// <summary>The same nodes, each level in name order rather than file order.</summary>
    public static IReadOnlyList<OutlineNode> SortedByName(IReadOnlyList<OutlineNode> nodes) =>
        nodes.OrderBy(n => n.Definition.Name, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.StartLine)
            .Select(n => n with { Children = SortedByName(n.Children) })
            .ToList();

    private sealed class Entry(SymbolDefinition definition, SymbolDetail? detail, int start, int end)
    {
        public SymbolDefinition Definition { get; } = definition;
        public SymbolDetail? Detail { get; } = detail;
        public int Start { get; } = start;
        public int End { get; } = end;
        public List<Entry> Children { get; } = [];

        public bool Covers(Entry other) => other.Start >= Start && other.Start <= End;
    }
}
