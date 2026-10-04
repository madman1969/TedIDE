namespace Tedide.Core.Navigation;

/// <summary>One step of a C expression such as <c>players[i].pos-&gt;x</c>, read left to right.</summary>
public enum AccessKind
{
    /// <summary>The name the expression starts from.</summary>
    Name,

    /// <summary><c>.name</c> or <c>-&gt;name</c>.</summary>
    Member,

    /// <summary><c>[...]</c> or a leading <c>*</c>.</summary>
    Dereference,

    /// <summary><c>(...)</c> after a function name.</summary>
    Call,
}

public sealed record AccessStep(AccessKind Kind, string? Name = null);

/// <summary>
/// What a file's code can see, for following <c>.</c> and <c>-&gt;</c> and showing a call's
/// signature: a name's declaration (innermost local first, then the file's own symbols, then those
/// of the files it includes, then the rest of the project's), the struct or union a type comes down
/// to through typedefs, and that struct's members. Works over any set of <see cref="FileSymbols"/> -
/// <see cref="SymbolIndex"/>'s for completion, <see cref="CodeNavigator"/>'s own for navigation.
/// </summary>
/// <param name="file">A file's symbols, if known.</param>
/// <param name="visibleFrom">The other files a file can see, nearest first: those it includes, then
/// the rest of the project.</param>
public sealed class CodeModel(Func<string, FileSymbols?> file, Func<string, IEnumerable<FileSymbols>> visibleFrom)
{
    /// <summary>The declaration <paramref name="name"/> means at <paramref name="line"/> of
    /// <paramref name="path"/>, with its detail - members never, since they need a struct to be
    /// named. A definition wins over a declaration in the same file.</summary>
    public (SymbolDefinition Definition, SymbolDetail? Detail)? Lookup(string path, int line, string name)
    {
        if (file(path) is { } current)
        {
            var local = current.Definitions
                .Where(d => d.Name == name && d.Scope is { } scope && scope.Contains(line) && d.Line <= line)
                .OrderByDescending(d => d.Scope!.Value.StartLine)
                .FirstOrDefault();
            if (local is not null)
                return (local, current.DetailOf(local));
            if (Global(current, name) is { } own)
                return own;
        }
        foreach (var other in visibleFrom(path))
            if (Global(other, name) is { } found)
                return found;
        return null;
    }

    private static (SymbolDefinition, SymbolDetail?)? Global(FileSymbols symbols, string name)
    {
        var matches = symbols.Definitions.Where(d => d.Name == name && !d.IsLocal && d.Kind != SymbolKind.Member && symbols.IsActive(d.Line)).ToList();
        var best = matches.FirstOrDefault(d => !d.IsDeclaration) ?? matches.FirstOrDefault();
        return best is null ? null : (best, symbols.DetailOf(best));
    }

    /// <summary>The type <paramref name="chain"/> ends up with, read at <paramref name="line"/> of
    /// <paramref name="path"/>, or null where it can't be followed.</summary>
    public CType? Resolve(string path, int line, IReadOnlyList<AccessStep> chain)
    {
        if (chain.Count == 0 || chain[0].Kind != AccessKind.Name || Lookup(path, line, chain[0].Name!) is not { } root)
            return null;

        var type = root.Detail?.Type;
        var isFunction = root.Definition.Kind is SymbolKind.Function or SymbolKind.Prototype;
        for (var i = 1; i < chain.Count && type is not null; i++)
        {
            var step = chain[i];
            switch (step.Kind)
            {
                case AccessKind.Call:
                    // A function's type here is already its return type; calling anything else
                    // (a function pointer) isn't followed.
                    if (!isFunction)
                        return null;
                    isFunction = false;
                    break;
                case AccessKind.Dereference:
                    type = type.Dereferenced;
                    break;
                case AccessKind.Member:
                    type = AggregateOf(type, path) is { } aggregate ? MemberType(aggregate, step.Name!, path) : null;
                    break;
            }
        }
        return isFunction ? null : type;
    }

    /// <summary>The struct or union <paramref name="type"/> comes down to through typedefs - its
    /// key, as members' <see cref="SymbolDetail.Container"/> names it - or null.</summary>
    public string? AggregateOf(CType type, string path)
    {
        for (var hops = 0; hops < 8; hops++)
        {
            if (type.IsAggregate)
                return type.Base;
            if (Typedef(path, type.Base) is not { } aliased)
                return null;
            type = aliased with { Indirection = aliased.Indirection + type.Indirection };
        }
        return null;
    }

    private CType? Typedef(string path, string name)
    {
        foreach (var symbols in Files(path))
            foreach (var definition in symbols.Definitions)
                if (definition.Kind == SymbolKind.Typedef && definition.Name == name && symbols.DetailOf(definition)?.Type is { } type)
                    return type;
        return null;
    }

    /// <summary>Every member of the struct or union <paramref name="aggregate"/>, as seen from
    /// <paramref name="path"/>, each once.</summary>
    public IEnumerable<(SymbolDefinition Definition, SymbolDetail Detail)> Members(string aggregate, string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbols in Files(path))
            foreach (var definition in symbols.Definitions)
                if (definition.Kind == SymbolKind.Member && symbols.DetailOf(definition) is { } detail
                    && detail.Container == aggregate && seen.Add(definition.Name))
                    yield return (definition, detail);
    }

    private CType? MemberType(string aggregate, string name, string path) =>
        Members(aggregate, path).FirstOrDefault(m => m.Definition.Name == name).Detail?.Type;

    /// <summary>The signature of the function or function-like macro <paramref name="name"/> means
    /// at <paramref name="line"/> of <paramref name="path"/>, if it has one.</summary>
    public CallSignature? SignatureOf(string path, int line, string name)
    {
        if (Lookup(path, line, name) is { Detail.Signature: { } signature })
            return signature;
        // A definition's own signature, if the nearest declaration had none (a prototype in a
        // header usually does - this catches a function defined further down the same file).
        foreach (var symbols in Files(path))
            foreach (var definition in symbols.Definitions)
                if (definition.Name == name && symbols.DetailOf(definition)?.Signature is { } found)
                    return found;
        return null;
    }

    /// <summary>The file itself, then what it can see.</summary>
    private IEnumerable<FileSymbols> Files(string path) =>
        (file(path) is { } current ? [current] : Enumerable.Empty<FileSymbols>()).Concat(visibleFrom(path));

    /// <summary>
    /// Reads the expression that ends at <paramref name="last"/> (inclusive) backwards - a name,
    /// then any <c>.x</c>, <c>-&gt;x</c>, <c>[...]</c> and <c>(...)</c> after it, and a <c>(*p)</c>
    /// - and returns it as steps left to right. Null if it isn't one this can follow.
    /// </summary>
    public static List<AccessStep>? ChainEndingAt(IReadOnlyList<SourceToken> tokens, int last)
    {
        var steps = new List<AccessStep>();
        var i = last;
        while (i >= 0)
        {
            var token = tokens[i];
            if (token.Is("]"))
            {
                i = Matching(tokens, i, "[", "]");
                if (i < 0)
                    return null;
                steps.Add(new AccessStep(AccessKind.Dereference));
                i--;
                continue;
            }
            if (token.Is(")"))
            {
                var open = Matching(tokens, i, "(", ")");
                if (open < 0)
                    return null;
                if (open > 0 && tokens[open - 1].Kind == TokenKind.Identifier && !CSymbolScanner.IsKeyword(tokens[open - 1].Text))
                {
                    steps.Add(new AccessStep(AccessKind.Call));
                    i = open - 1;
                    continue;
                }
                // "(*p)" - a dereference of what's inside.
                if (open + 2 < i && tokens[open + 1].Is("*") && Slice(tokens, open + 2, i) is var inside
                    && ChainEndingAt(inside, inside.Count - 1) is { } inner)
                {
                    // inner is already left to right; what's been read so far (after the ")") is
                    // right to left.
                    steps.Reverse();
                    return [.. inner, new AccessStep(AccessKind.Dereference), .. steps];
                }
                return null;
            }
            if (token.Kind != TokenKind.Identifier || CSymbolScanner.IsKeyword(token.Text))
                return null;

            // A name: a member if "." or "->" comes before it, else where the expression starts.
            if (i >= 1 && tokens[i - 1].Is("."))
            {
                steps.Add(new AccessStep(AccessKind.Member, token.Text));
                i -= 2;
                continue;
            }
            if (i >= 2 && tokens[i - 1].Is(">") && tokens[i - 2].Is("-") && tokens[i - 2].EndColumn == tokens[i - 1].Column)
            {
                steps.Add(new AccessStep(AccessKind.Member, token.Text));
                i -= 3;
                continue;
            }
            steps.Add(new AccessStep(AccessKind.Name, token.Text));
            steps.Reverse();
            return steps;
        }
        return null;
    }

    private static int Matching(IReadOnlyList<SourceToken> tokens, int close, string open, string closing)
    {
        var depth = 0;
        for (var i = close; i >= 0; i--)
        {
            if (tokens[i].Is(closing))
                depth++;
            else if (tokens[i].Is(open) && --depth == 0)
                return i;
        }
        return -1;
    }

    /// <summary>Slices a token list - lists don't take ranges directly.</summary>
    private static IReadOnlyList<SourceToken> Slice(IReadOnlyList<SourceToken> tokens, int start, int end) =>
        tokens.Skip(start).Take(end - start).ToList();
}
