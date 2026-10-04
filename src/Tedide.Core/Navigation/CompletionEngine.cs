namespace Tedide.Core.Navigation;

/// <summary>A suggestion: what's inserted, what it is ("function", "member", ...), and how near the
/// caret it comes from (lower is nearer - shown first).</summary>
public sealed record CompletionCandidate(string Name, string Kind, int Rank);

/// <summary>A call's signature while typing its arguments, with the argument the caret is in.</summary>
public sealed record SignatureHint(string Text, int ActiveStart, int ActiveLength);

/// <summary>
/// Code completion and signature help over a <see cref="SymbolIndex"/>. Everything here is asked on
/// keystrokes, so it only reads the index and the text before the caret - no files, no re-scanning.
/// </summary>
public static class CompletionEngine
{
    /// <summary>Fewer letters than this, and suggestions only come when asked for (Ctrl+Space).</summary>
    public const int AutomaticPrefixLength = 2;

    /// <summary>At most this many suggestions.</summary>
    private const int MaxCandidates = 100;

    /// <summary>
    /// Suggestions for the word fragment <paramref name="prefix"/> just before
    /// <paramref name="offset"/> in <paramref name="text"/>, the editor's text of
    /// <paramref name="path"/>. Empty in a comment or string, after a number, and - unless
    /// <paramref name="requested"/> - while the fragment is shorter than
    /// <see cref="AutomaticPrefixLength"/> (except straight after <c>.</c> or <c>-&gt;</c>).
    /// </summary>
    /// <param name="cpu">The project's CPU ("6502", "65C02", "65816"...), for assembly mnemonics.</param>
    public static IReadOnlyList<CompletionCandidate> Complete(SymbolIndex index, string path, string text, int offset,
        string prefix, bool requested, string cpu = "6502")
    {
        offset = Math.Clamp(offset, 0, text.Length);
        if (prefix.Length > offset || (prefix.Length > 0 && char.IsAsciiDigit(prefix[0])))
            return [];

        var line = LineOf(text, offset);
        return SourceTokenizer.LanguageOf(path) switch
        {
            SourceLanguage.C => CompleteC(index, path, text, offset, prefix, requested, line),
            SourceLanguage.Assembly => CompleteAssembly(index, path, text, offset, prefix, requested, line, cpu),
            _ => [],
        };
    }

    private static IReadOnlyList<CompletionCandidate> CompleteC(SymbolIndex index, string path, string text, int offset,
        string prefix, bool requested, int line)
    {
        var prefixStart = offset - prefix.Length;
        var state = CStateAt(text, prefixStart);
        if (state.InCommentOrString || state.InInclude)
            return [];

        var model = Model(index);

        // After "." or "->": the members of whatever's on the left.
        if (MemberOperatorBefore(text, prefixStart) is { } operatorStart)
        {
            var tokens = SourceTokenizer.TokenizeC(text[LineStartBefore(text, operatorStart, 3)..operatorStart]);
            if (tokens.Count == 0 || CodeModel.ChainEndingAt(tokens, tokens.Count - 1) is not { } chain
                || model.Resolve(path, line, chain) is not { } type || model.AggregateOf(type, path) is not { } aggregate)
                return [];
            return Finish(model.Members(aggregate, path)
                .Where(m => Matches(m.Definition.Name, prefix))
                .Select(m => new CompletionCandidate(m.Definition.Name, "member", 0)), prefix);
        }

        if (!requested && prefix.Length < AutomaticPrefixLength)
            return [];

        // After "struct"/"union"/"enum": tag names only.
        var previousWord = WordBefore(text, prefixStart);
        var wantTags = previousWord is "struct" or "union" or "enum";

        var candidates = new List<CompletionCandidate>();
        void Offer(FileSymbols symbols, int rank, Func<SymbolDefinition, bool> visible)
        {
            foreach (var definition in symbols.Definitions)
            {
                if (!visible(definition) || !Matches(definition.Name, prefix) || (definition.Kind == SymbolKind.Tag) != wantTags
                    || definition.Kind is SymbolKind.Member or SymbolKind.File || !symbols.IsActive(definition.Line))
                    continue;
                candidates.Add(new CompletionCandidate(definition.Name, KindText(definition.Kind), rank));
            }
        }

        var current = index.Get(path);
        if (current is not null)
        {
            // Parameters and locals whose block the caret is in.
            Offer(current, 0, d => d.Scope is { } scope && scope.Contains(line) && d.Line <= line && d.Kind is SymbolKind.Parameter or SymbolKind.LocalVariable);
            Offer(current, 1, d => !d.IsLocal);
        }
        var reachable = index.Reachable(path);
        foreach (var included in reachable)
            Offer(included, 2, d => !d.IsLocal);
        var nearby = new HashSet<string>(reachable.Select(f => f.Path), StringComparer.OrdinalIgnoreCase) { path };
        foreach (var project in index.Projects().Where(p => !nearby.Contains(p.Path) && p.Language == SourceLanguage.C))
            Offer(project, 3, d => !d.IsLocal);

        if (!wantTags)
            candidates.AddRange(CSymbolScanner.Keywords.Where(k => Matches(k, prefix)).Select(k => new CompletionCandidate(k, "keyword", 4)));
        return Finish(candidates, prefix);
    }

    private static IReadOnlyList<CompletionCandidate> CompleteAssembly(SymbolIndex index, string path, string text, int offset,
        string prefix, bool requested, int line, string cpu)
    {
        var prefixStart = offset - prefix.Length;
        var lineStart = LineStartBefore(text, prefixStart, 0);
        var lineText = text[lineStart..prefixStart];
        if (InAssemblyCommentOrString(lineText))
            return [];

        var before = prefixStart > lineStart ? text[prefixStart - 1] : '\n';
        var upper = prefix.Length > 0 && prefix.All(c => !char.IsAsciiLetterLower(c)) && prefix.Any(char.IsAsciiLetterUpper);

        // ".pro|" - a control command, offered without the dot already typed.
        if (before == '.')
            return Finish(AssemblyDirectives
                .Where(d => Matches(d, prefix))
                .Select(d => new CompletionCandidate(upper ? d.ToUpperInvariant() : d, "directive", 0)), prefix);

        var current = index.Get(path);
        // "@lo|" - the cheap locals in this stretch between ordinary labels.
        if (before == '@')
            return current is null ? [] : Finish(current.Definitions
                .Where(d => d.Name.StartsWith('@') && d.Scope is { } scope && scope.Contains(line) && Matches(d.Name[1..], prefix))
                .Select(d => new CompletionCandidate(d.Name[1..], "local label", 0)), prefix);

        if (!requested && prefix.Length < AutomaticPrefixLength)
            return [];

        var candidates = new List<CompletionCandidate>();
        var tokens = SourceTokenizer.TokenizeAssembly(lineText);
        // A leading "label:" doesn't count - the instruction comes after it.
        if (tokens.Count >= 2 && tokens[0].Kind == TokenKind.Identifier && tokens[1].Is(":"))
            tokens = tokens.Skip(tokens.Count >= 3 && tokens[2].Is(":") ? 3 : 2).ToList();
        var isInstruction = tokens.Count == 0;

        void Offer(FileSymbols symbols, int rank, Func<SymbolDefinition, bool> wanted)
        {
            foreach (var definition in symbols.Definitions)
                if (!definition.IsLocal && wanted(definition) && Matches(definition.Name, prefix))
                    candidates.Add(new CompletionCandidate(definition.Name, KindText(definition.Kind), rank));
        }

        Func<SymbolDefinition, bool> wanted = isInstruction
            ? d => d.Kind == SymbolKind.Macro
            : d => d.Kind is not (SymbolKind.Member or SymbolKind.File or SymbolKind.Parameter);
        if (current is not null)
            Offer(current, 1, wanted);
        var reachable = index.Reachable(path);
        foreach (var included in reachable)
            Offer(included, 2, wanted);
        var nearby = new HashSet<string>(reachable.Select(f => f.Path), StringComparer.OrdinalIgnoreCase) { path };
        foreach (var project in index.Projects().Where(p => !nearby.Contains(p.Path)))
        {
            if (project.Language == SourceLanguage.Assembly)
                Offer(project, 3, wanted);
            else if (!isInstruction)
                // cc65 gives a C global an underscore in assembly.
                foreach (var definition in project.Definitions)
                    if (!definition.IsLocal && definition.Kind is SymbolKind.Function or SymbolKind.Variable
                        && Matches("_" + definition.Name, prefix))
                        candidates.Add(new CompletionCandidate("_" + definition.Name, "C " + KindText(definition.Kind), 4));
        }

        if (isInstruction)
            candidates.AddRange(Mnemonics(cpu)
                .Where(m => Matches(m, prefix))
                .Select(m => new CompletionCandidate(upper ? m.ToUpperInvariant() : m, "instruction", 0)));
        return Finish(candidates, prefix);
    }

    /// <summary>
    /// The signature of the call the caret is inside, with the argument it's in picked out - or null
    /// outside a call, in a comment or string, or for a function the index doesn't know.
    /// </summary>
    public static SignatureHint? SignatureAt(SymbolIndex index, string path, string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        if (SourceTokenizer.LanguageOf(path) != SourceLanguage.C || CStateAt(text, offset).InCommentOrString)
            return null;

        // A call rarely spans more than a few lines.
        var tokens = SourceTokenizer.TokenizeC(text[LineStartBefore(text, offset, 10)..offset]);
        var depth = 0;
        var commas = 0;
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (token.Is(")") || token.Is("]"))
                depth++;
            else if (token.Is("(") || token.Is("["))
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }
                if (!token.Is("(") || i == 0 || tokens[i - 1].Kind != TokenKind.Identifier || CSymbolScanner.IsKeyword(tokens[i - 1].Text))
                    return null;
                var signature = Model(index).SignatureOf(path, LineOf(text, offset), tokens[i - 1].Text);
                if (signature is null)
                    return null;
                var active = commas < signature.Parameters.Count ? signature.Parameters[commas] : (0, 0);
                return new SignatureHint(signature.Text, active.Item1, active.Item2);
            }
            else if (depth == 0 && token.Is(","))
                commas++;
            else if (depth == 0 && token.Text is ";" or "{" or "}")
                return null;
        }
        return null;
    }

    /// <summary>The index's files as a <see cref="CodeModel"/>: a file sees what it includes, then
    /// the rest of the project.</summary>
    private static CodeModel Model(SymbolIndex index) =>
        new(index.Get, path =>
        {
            var reachable = index.Reachable(path);
            var nearby = new HashSet<string>(reachable.Select(f => f.Path), StringComparer.OrdinalIgnoreCase) { path };
            return reachable.Concat(index.Projects().Where(p => !nearby.Contains(p.Path)));
        });

    /// <summary>Each name once (its nearest kind), exact-case prefix matches first, nearest first,
    /// then alphabetically.</summary>
    private static IReadOnlyList<CompletionCandidate> Finish(IEnumerable<CompletionCandidate> candidates, string prefix) =>
        candidates
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .Select(g => g.OrderBy(c => c.Rank).First())
            .OrderBy(c => c.Name.StartsWith(prefix, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(c => c.Rank)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .ToList();

    private static bool Matches(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length;

    public static string KindText(SymbolKind kind) => kind switch
    {
        SymbolKind.Function or SymbolKind.Prototype => "function",
        SymbolKind.Variable or SymbolKind.ExternVariable => "variable",
        SymbolKind.LocalVariable => "local",
        SymbolKind.Parameter => "parameter",
        SymbolKind.Macro => "macro",
        SymbolKind.Typedef => "type",
        SymbolKind.Tag => "tag",
        SymbolKind.EnumConstant => "enum constant",
        SymbolKind.Member => "member",
        SymbolKind.Label => "label",
        SymbolKind.Constant => "constant",
        SymbolKind.Import => "import",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>Where a C position is: in a comment or string (where nothing is suggested), or on an
    /// #include line (where a header name, not a symbol, goes).</summary>
    private readonly record struct CState(bool InCommentOrString, bool InInclude);

    private static CState CStateAt(string text, int offset)
    {
        var inLineComment = false;
        var inBlockComment = false;
        char quote = '\0';
        for (var i = 0; i < offset; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (inLineComment)
            {
                if (c == '\n')
                    inLineComment = false;
            }
            else if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
            }
            else if (quote != '\0')
            {
                if (c == '\\')
                    i++;
                else if (c == quote || c == '\n')
                    quote = '\0';
            }
            else if (c == '/' && next == '/')
                inLineComment = true;
            else if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
            }
            else if (c is '"' or '\'')
                quote = c;
        }

        var lineText = text[LineStartBefore(text, offset, 0)..offset].TrimStart();
        var inInclude = lineText.StartsWith('#') && lineText[1..].TrimStart().StartsWith("include", StringComparison.Ordinal);
        return new CState(inLineComment || inBlockComment || quote != '\0', inInclude);
    }

    /// <summary>Whether the caret, partway along an assembly line, is after a ';' comment or inside a string.</summary>
    private static bool InAssemblyCommentOrString(string lineText)
    {
        char quote = '\0';
        foreach (var c in lineText)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c == ';')
                return true;
            else if (c is '"' or '\'')
                quote = c;
        }
        return quote != '\0';
    }

    /// <summary>Where the "." or "->" before <paramref name="position"/> starts (spaces allowed
    /// between), or null if there isn't one - "1." in a number doesn't count.</summary>
    private static int? MemberOperatorBefore(string text, int position)
    {
        var i = position - 1;
        while (i >= 0 && text[i] is ' ' or '\t')
            i--;
        if (i >= 0 && text[i] == '.' && !(i >= 1 && char.IsAsciiDigit(text[i - 1])))
            return i;
        if (i >= 1 && text[i] == '>' && text[i - 1] == '-')
            return i - 1;
        return null;
    }

    /// <summary>The word just before <paramref name="position"/>, past any spaces, or null.</summary>
    private static string? WordBefore(string text, int position)
    {
        var end = position;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            end--;
        var start = end;
        while (start > 0 && (char.IsAsciiLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            start--;
        return end > start ? text[start..end] : null;
    }

    /// <summary>The start of the line <paramref name="linesBack"/> lines above the one
    /// <paramref name="position"/> is on.</summary>
    private static int LineStartBefore(string text, int position, int linesBack)
    {
        var start = position > 0 ? text.LastIndexOf('\n', position - 1) + 1 : 0;
        for (var n = 0; n < linesBack && start > 0; n++)
            start = start >= 2 ? text.LastIndexOf('\n', start - 2) + 1 : 0;
        return start;
    }

    private static int LineOf(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset; i++)
            if (text[i] == '\n')
                line++;
        return line;
    }

    private static readonly string[] Mnemonics6502 =
    [
        "adc", "and", "asl", "bcc", "bcs", "beq", "bit", "bmi", "bne", "bpl", "brk", "bvc", "bvs", "clc", "cld", "cli",
        "clv", "cmp", "cpx", "cpy", "dec", "dex", "dey", "eor", "inc", "inx", "iny", "jmp", "jsr", "lda", "ldx", "ldy",
        "lsr", "nop", "ora", "pha", "php", "pla", "plp", "rol", "ror", "rti", "rts", "sbc", "sec", "sed", "sei", "sta",
        "stx", "sty", "tax", "tay", "tsx", "txa", "txs", "tya",
    ];

    private static readonly string[] Mnemonics65C02 = ["bra", "phx", "phy", "plx", "ply", "stz", "trb", "tsb"];

    private static readonly string[] Mnemonics65816 =
    [
        "brl", "cop", "jml", "jsl", "mvn", "mvp", "pea", "pei", "per", "phb", "phd", "phk", "plb", "pld", "rep", "rtl",
        "sep", "stp", "tcd", "tcs", "tdc", "tsc", "txy", "tyx", "wai", "wdm", "xba", "xce",
    ];

    /// <summary>The instructions <paramref name="cpu"/> has: the 6502's, plus the 65C02's and the 65816's where it has them.</summary>
    internal static IEnumerable<string> Mnemonics(string cpu)
    {
        var lower = cpu.ToLowerInvariant();
        IEnumerable<string> result = Mnemonics6502;
        if (lower.Contains("c02") || lower.Contains("816"))
            result = result.Concat(Mnemonics65C02);
        if (lower.Contains("816"))
            result = result.Concat(Mnemonics65816);
        return result;
    }

    /// <summary>ca65's control commands and pseudo functions, without their leading dot.</summary>
    internal static readonly string[] AssemblyDirectives =
    [
        "a16", "a8", "addr", "align", "asciiz", "assert", "autoimport", "bankbyte", "bankbytes", "blank", "bss", "byte",
        "case", "charmap", "code", "concat", "condes", "const", "constructor", "cpu", "data", "dbyt", "debuginfo", "define",
        "defined", "delmac", "destructor", "dword", "else", "elseif", "end", "endenum", "endif", "endmac", "endmacro",
        "endproc", "endrep", "endrepeat", "endscope", "endstruct", "endunion", "enum", "error", "exitmac", "exitmacro",
        "export", "exportzp", "faraddr", "fatal", "feature", "fileopt", "forceimport", "global", "globalzp", "hibyte",
        "hibytes", "i16", "i8", "ident", "if", "ifblank", "ifconst", "ifdef", "ifnblank", "ifndef", "ifnref", "ifp02",
        "ifp816", "ifpc02", "ifpsc02", "ifref", "import", "importzp", "incbin", "include", "interruptor", "left", "list",
        "listbytes", "lobyte", "lobytes", "local", "localchar", "mac", "macpack", "macro", "match", "mid", "org", "out",
        "p02", "p816", "pagelen", "pc02", "popcpu", "popseg", "proc", "psc02", "pushcpu", "pushseg", "referenced", "reloc",
        "repeat", "res", "right", "rodata", "scope", "segment", "set", "setcpu", "sizeof", "smart", "sprintf", "strat",
        "string", "strlen", "struct", "tag", "tcount", "undef", "undefine", "union", "warning", "word", "xmatch", "zeropage",
    ];
}
