namespace Tedide.Core.Navigation;

/// <summary>
/// Finds the symbols a C file defines or declares: functions and prototypes, file-scope variables,
/// <c>#define</c> macros, typedefs, struct/union/enum tags and members, enum constants, and each
/// function's parameters and local variables (scoped to the block that declares them). A ctags-style
/// heuristic over the token stream rather than a real parser: no preprocessing, no type checking.
/// It's built for cc65-era C89 and stays useful on code that's half-typed or doesn't compile yet.
/// <para>
/// <see cref="ScanDetailed"/> also records each symbol's type, which struct or union a member
/// belongs to, and each function's signature - enough to follow <c>p-&gt;x.y</c> and to show a
/// call's parameters, for code completion.
/// </para>
/// </summary>
public static class CSymbolScanner
{
    /// <summary>Words that can start or appear in a declaration's type, and so are never its name.
    /// Includes cc65's own calling-convention and address-size qualifiers.</summary>
    private static readonly HashSet<string> TypeKeywords =
    [
        "void", "char", "short", "int", "long", "float", "double", "signed", "unsigned",
        "const", "volatile", "static", "extern", "register", "auto", "typedef", "inline",
        "struct", "union", "enum",
        "__fastcall__", "__cdecl__", "fastcall", "cdecl", "__near__", "__far__", "near", "far",
    ];

    private static readonly HashSet<string> StatementKeywords =
    [
        "if", "else", "while", "for", "do", "switch", "case", "default", "break", "continue",
        "return", "goto", "sizeof", "asm", "__asm__",
    ];

    /// <summary>Words in a declaration that say how it's stored or called, not what type it is.</summary>
    private static readonly HashSet<string> Qualifiers =
    [
        "const", "volatile", "static", "extern", "register", "auto", "typedef", "inline",
        "__fastcall__", "__cdecl__", "fastcall", "cdecl", "__near__", "__far__", "near", "far",
    ];

    public static bool IsKeyword(string word) => TypeKeywords.Contains(word) || StatementKeywords.Contains(word);

    /// <summary>Every C keyword cc65 knows, for completion.</summary>
    public static IEnumerable<string> Keywords => TypeKeywords.Concat(StatementKeywords);

    private enum FrameKind
    {
        Function,
        Block,
        Aggregate,
        Enum,
    }

    private sealed class Frame(FrameKind kind, List<SourceToken> savedStatement, string? aggregateKey = null)
    {
        public FrameKind Kind { get; } = kind;

        /// <summary>For a struct/union/enum body: the declaration it interrupted (<c>typedef struct</c>,
        /// <c>struct point</c>, ...), which carries on after the closing brace.</summary>
        public List<SourceToken> SavedStatement { get; } = savedStatement;

        /// <summary>For a struct or union body, the key its members are filed under.</summary>
        public string? AggregateKey { get; } = aggregateKey;

        public List<(SourceToken Token, SymbolKind Kind, CType? Type)> Locals { get; } = [];
    }

    public static List<SymbolDefinition> Scan(string path, IReadOnlyList<SourceToken> tokens) =>
        ScanDetailed(path, tokens).Definitions;

    public static CScan ScanDetailed(string path, IReadOnlyList<SourceToken> tokens)
    {
        var definitions = new List<SymbolDefinition>();
        var details = new Dictionary<SymbolDefinition, SymbolDetail>();
        ScanDirectives(path, tokens, definitions, details);

        var code = tokens.Where(t => t.Directive == 0).ToList();
        var frames = new Stack<Frame>();
        var statement = new List<SourceToken>();
        var depth = 0;
        // The "{}" marker a struct or union body leaves in its declaration, by position, so the
        // declaration's type can name it.
        var aggregateMarkers = new Dictionary<(int Line, int Column), string>();

        SymbolDefinition Add(SourceToken token, SymbolKind kind, SourceScope? scope = null, SymbolDetail? detail = null)
        {
            var definition = new SymbolDefinition(token.Text, kind, path, token.Line, token.Column, scope);
            definitions.Add(definition);
            if (detail is not null)
                details[definition] = detail;
            return definition;
        }

        FrameKind? Context() => frames.Count == 0 ? null : frames.Peek().Kind;

        void CloseFrame(int endLine)
        {
            var frame = frames.Pop();
            foreach (var (token, kind, type) in frame.Locals)
                Add(token, kind, new SourceScope(token.Line, endLine), type is null ? null : new SymbolDetail(type));
        }

        CType? TypeOf(List<SourceToken> specifiers, List<SourceToken> declarator, SourceToken name) =>
            BaseType(specifiers, aggregateMarkers) is { } baseName ? new CType(baseName, Indirection(declarator, name)) : null;

        void EndStatement()
        {
            switch (Context())
            {
                case null:
                    AddDeclarations(statement, isFileScope: true);
                    break;
                case FrameKind.Aggregate:
                {
                    var specifiers = Specifiers(statement);
                    var container = frames.Peek().AggregateKey;
                    foreach (var (name, _, segment) in Declarators(statement))
                        Add(name, SymbolKind.Member, detail: new SymbolDetail(TypeOf(specifiers, segment, name), container));
                    break;
                }
                case FrameKind.Enum:
                    AddEnumConstant(statement);
                    break;
                default:
                    if (IsLocalDeclaration(statement))
                        AddDeclarations(statement, isFileScope: false);
                    break;
            }
            statement.Clear();
        }

        void AddDeclarations(List<SourceToken> tokens, bool isFileScope)
        {
            var isTypedef = tokens.Any(t => t.Is("typedef"));
            var isExtern = tokens.Any(t => t.Is("extern"));
            var specifiers = Specifiers(tokens);
            foreach (var (name, isFunction, segment) in Declarators(tokens))
            {
                var type = TypeOf(specifiers, segment, name);
                if (isTypedef)
                    Add(name, SymbolKind.Typedef, detail: new SymbolDetail(type));
                else if (isFunction)
                {
                    Add(name, SymbolKind.Prototype, detail: new SymbolDetail(type, Signature: Signature(tokens, name)));
                    // A prototype's parameter names mean nothing outside it, but recording them
                    // (scoped to the prototype) stops them counting as uses of a global "width".
                    var scope = new SourceScope(name.Line, tokens[^1].Line);
                    foreach (var (parameter, parameterType) in PrototypeParameters(tokens, name, aggregateMarkers))
                        Add(parameter, SymbolKind.Parameter, scope, parameterType is null ? null : new SymbolDetail(parameterType));
                }
                else if (isExtern)
                    Add(name, SymbolKind.ExternVariable, detail: new SymbolDetail(type));
                else if (isFileScope)
                    Add(name, SymbolKind.Variable, detail: new SymbolDetail(type));
                else
                    frames.Peek().Locals.Add((name, SymbolKind.LocalVariable, type));
            }
        }

        void AddEnumConstant(List<SourceToken> tokens)
        {
            if (tokens.Count > 0 && tokens[0].Kind == TokenKind.Identifier && !IsKeyword(tokens[0].Text))
                Add(tokens[0], SymbolKind.EnumConstant);
        }

        for (var i = 0; i < code.Count; i++)
        {
            var token = code[i];
            if (token.Kind != TokenKind.Punctuation)
            {
                statement.Add(token);
                continue;
            }

            switch (token.Text)
            {
                case "(" or "[":
                    depth++;
                    statement.Add(token);
                    break;
                case ")" or "]":
                    depth = Math.Max(0, depth - 1);
                    statement.Add(token);
                    break;
                case ";" when depth == 0:
                    EndStatement();
                    break;
                case "," when depth == 0 && Context() == FrameKind.Enum:
                    AddEnumConstant(statement);
                    statement.Clear();
                    break;
                case "{":
                {
                    depth = 0;
                    var marker = token with { Text = "{}" };
                    if (TopLevelIndex(statement, "=") >= 0)
                    {
                        // An initializer: nothing inside it declares anything. Skip to its closing
                        // brace and carry on with the same declaration.
                        var nesting = 1;
                        while (nesting > 0 && ++i < code.Count)
                        {
                            if (code[i].Is("{"))
                                nesting++;
                            else if (code[i].Is("}"))
                                nesting--;
                        }
                        statement.Add(marker);
                        break;
                    }

                    if (AggregateKeyword(statement) is { } aggregate)
                    {
                        var hasTag = statement[^1].Kind == TokenKind.Identifier && statement.Count >= 2 && statement[^2].Is(aggregate);
                        if (hasTag)
                            Add(statement[^1], SymbolKind.Tag);
                        string? key = aggregate == "enum" ? null
                            : hasTag ? $"{aggregate} {statement[^1].Text}"
                            : SymbolDetail.AnonymousKey(path, token.Line, token.Column);
                        if (key is not null)
                            aggregateMarkers[(marker.Line, marker.Column)] = key;
                        frames.Push(new Frame(aggregate == "enum" ? FrameKind.Enum : FrameKind.Aggregate, [.. statement, marker], key));
                        statement.Clear();
                        break;
                    }

                    if (Context() is null && FunctionDefinition(statement) is { } function)
                    {
                        Add(function.Name, SymbolKind.Function, detail: new SymbolDetail(
                            TypeOf(Specifiers(statement), statement[..(statement.IndexOf(function.Name) + 1)], function.Name),
                            Signature: Signature(statement, function.Name)));
                        var frame = new Frame(FrameKind.Function, []);
                        foreach (var (parameter, parameterType) in PrototypeParameters(statement, function.Name, aggregateMarkers))
                            frame.Locals.Add((parameter, SymbolKind.Parameter, parameterType));
                        frames.Push(frame);
                        statement.Clear();
                        break;
                    }

                    // An ordinary block: a function body we couldn't recognise, or if/for/while/a bare {}.
                    frames.Push(new Frame(FrameKind.Block, []));
                    statement.Clear();
                    break;
                }
                case "}":
                {
                    depth = 0;
                    if (Context() == FrameKind.Enum)
                        AddEnumConstant(statement);
                    statement.Clear();
                    if (frames.Count == 0)
                        break;

                    var kind = frames.Peek().Kind;
                    var saved = frames.Peek().SavedStatement;
                    CloseFrame(token.Line);
                    if (kind is FrameKind.Aggregate or FrameKind.Enum)
                        statement.AddRange(saved);
                    break;
                }
                default:
                    statement.Add(token);
                    break;
            }
        }

        // Unbalanced braces (half-typed code): close whatever's still open at the end of the file.
        var lastLine = tokens.Count > 0 ? tokens[^1].Line : 1;
        while (frames.Count > 0)
            CloseFrame(lastLine);

        return new CScan(definitions, details);
    }

    /// <summary><c>#define NAME</c>, plus a function-like macro's own parameters, visible only on its
    /// own (possibly continued) line, and its signature.</summary>
    private static void ScanDirectives(string path, IReadOnlyList<SourceToken> tokens, List<SymbolDefinition> definitions,
        Dictionary<SymbolDefinition, SymbolDetail> details)
    {
        foreach (var directive in tokens.Where(t => t.Directive != 0).GroupBy(t => t.Directive))
        {
            var line = directive.ToList();
            if (line.Count < 3 || !line[0].Is("#") || !line[1].Is("define") || line[2].Kind != TokenKind.Identifier)
                continue;

            var name = line[2];
            var macro = new SymbolDefinition(name.Text, SymbolKind.Macro, path, name.Line, name.Column);
            definitions.Add(macro);

            // "#define F(a, b)" - the "(" has to touch the name, or it's an object-like macro whose
            // body happens to start with a parenthesis.
            if (line.Count > 3 && line[3].Is("(") && line[3].Column == name.EndColumn && line[3].Line == name.Line)
            {
                var scope = new SourceScope(name.Line, line[^1].Line);
                var close = 4;
                for (; close < line.Count && !line[close].Is(")"); close++)
                    if (line[close].Kind == TokenKind.Identifier)
                        definitions.Add(new SymbolDefinition(line[close].Text, SymbolKind.Parameter, path, line[close].Line, line[close].Column, scope));
                details[macro] = new SymbolDetail(Signature: BuildSignature(line[2..Math.Min(close + 1, line.Count)]));
            }
        }
    }

    /// <summary>Whether a statement inside a function body declares variables, rather than doing
    /// something: it starts with a type word, or reads "Type name" / "Type *name" for a typedef'd
    /// type.</summary>
    private static bool IsLocalDeclaration(List<SourceToken> statement)
    {
        if (statement.Count < 2 || statement[0].Kind != TokenKind.Identifier || StatementKeywords.Contains(statement[0].Text))
            return false;
        if (TypeKeywords.Contains(statement[0].Text))
            return true;
        if (statement[1].Kind == TokenKind.Identifier && !IsKeyword(statement[1].Text))
            return true;
        return statement.Count >= 3 && statement[1].Is("*") && statement[2].Kind == TokenKind.Identifier
            && (statement.Count == 3 || statement[3].Text is "=" or "," or "[");
    }

    /// <summary>The struct/union/enum keyword a statement ends with (optionally followed by a tag
    /// name) - i.e. the brace about to open is that aggregate's body.</summary>
    private static string? AggregateKeyword(List<SourceToken> statement)
    {
        static bool IsAggregate(SourceToken t) => t.Text is "struct" or "union" or "enum";

        if (statement.Count >= 1 && IsAggregate(statement[^1]))
            return statement[^1].Text;
        if (statement.Count >= 2 && statement[^1].Kind == TokenKind.Identifier && IsAggregate(statement[^2]))
            return statement[^2].Text;
        return null;
    }

    private sealed record FunctionSignature(SourceToken Name, List<SourceToken> Parameters);

    /// <summary>A statement that ends just before a body's "{" with "name(...)" is a function
    /// definition. Returns its name and the names of its parameters.</summary>
    private static FunctionSignature? FunctionDefinition(List<SourceToken> statement)
    {
        if (statement.Count < 3 || !statement[^1].Is(")"))
            return null;

        var open = TopLevelIndex(statement, "(");
        if (open < 1 || statement[open - 1].Kind != TokenKind.Identifier || IsKeyword(statement[open - 1].Text))
            return null;

        var close = MatchingClose(statement, open);
        var parameters = new List<SourceToken>();
        foreach (var segment in SplitTopLevel(statement[(open + 1)..close], ","))
            if (DeclaratorName(segment) is { } parameter)
                parameters.Add(parameter.Name);

        return new FunctionSignature(statement[open - 1], parameters);
    }

    /// <summary>The named parameters in the parameter list straight after a function's name, with
    /// their types. An unnamed one ("int") names nothing; a lone typedef'd type is indistinguishable
    /// from a name, and harmless either way since it's scoped to the function.</summary>
    private static IEnumerable<(SourceToken Name, CType? Type)> PrototypeParameters(List<SourceToken> statement, SourceToken name,
        Dictionary<(int, int), string> aggregateMarkers)
    {
        var open = statement.IndexOf(name) + 1;
        if (open < 1 || open >= statement.Count || !statement[open].Is("("))
            yield break;

        var close = MatchingClose(statement, open);
        foreach (var segment in SplitTopLevel(statement[(open + 1)..Math.Min(close, statement.Count)], ","))
        {
            if (DeclaratorName(segment) is not { } parameter)
                continue;
            var specifiers = segment[..segment.IndexOf(parameter.Name)].Where(t => !t.Is("*")).ToList();
            var type = BaseType(specifiers, aggregateMarkers) is { } baseName ? new CType(baseName, Indirection(segment, parameter.Name)) : null;
            yield return (parameter.Name, type);
        }
    }

    /// <summary>Every name a declaration statement declares, with the tokens of its own declarator:
    /// "int a, *b, c[3]" declares a, b and c.</summary>
    private static IEnumerable<(SourceToken Name, bool IsFunction, List<SourceToken> Segment)> Declarators(List<SourceToken> statement)
    {
        foreach (var segment in SplitTopLevel(statement, ","))
        {
            var equals = TopLevelIndex(segment, "=");
            var declarator = equals >= 0 ? segment[..equals] : segment;
            if (DeclaratorName(declarator) is { } found)
                yield return (found.Name, found.IsFunction, declarator);
        }
    }

    /// <summary>
    /// The name one declarator declares. "f(...)" is a function; "(*fp)(...)" is a pointer variable
    /// named fp; otherwise it's the last plain word outside brackets - skipping type keywords and the
    /// tag after struct/union/enum, so "struct point p" names p and "struct point" names nothing.
    /// </summary>
    private static (SourceToken Name, bool IsFunction)? DeclaratorName(List<SourceToken> segment)
    {
        var open = TopLevelIndex(segment, "(");
        if (open >= 0)
        {
            if (open + 1 < segment.Count && segment[open + 1].Is("*"))
            {
                var close = MatchingClose(segment, open);
                for (var i = open + 1; i < close; i++)
                    if (segment[i].Kind == TokenKind.Identifier && !IsKeyword(segment[i].Text))
                        return (segment[i], false);
                return null;
            }

            if (open >= 1 && segment[open - 1].Kind == TokenKind.Identifier && !IsKeyword(segment[open - 1].Text)
                && !(open >= 2 && segment[open - 2].Text is "struct" or "union" or "enum"))
                return (segment[open - 1], true);
            return null;
        }

        SourceToken? name = null;
        var depth = 0;
        for (var i = 0; i < segment.Count; i++)
        {
            var token = segment[i];
            if (token.Text is "[" or "(")
                depth++;
            else if (token.Text is "]" or ")")
                depth--;
            else if (depth == 0 && token.Kind == TokenKind.Identifier && !IsKeyword(token.Text)
                && !(i > 0 && segment[i - 1].Text is "struct" or "union" or "enum"))
                name = token;
        }
        return name is { } found ? (found, false) : null;
    }

    /// <summary>The words a declaration statement's type is made of: everything in its first
    /// declarator before the declared name, less the pointer stars that belong to that name.</summary>
    private static List<SourceToken> Specifiers(List<SourceToken> statement)
    {
        var first = SplitTopLevel(statement, ",").FirstOrDefault() ?? [];
        var equals = TopLevelIndex(first, "=");
        var declarator = equals >= 0 ? first[..equals] : first;
        if (DeclaratorName(declarator) is not { } name)
            return declarator;
        var end = declarator.IndexOf(name.Name);
        // "(*fp)(...)": the type ends before the parenthesis that opens the declarator.
        var paren = TopLevelIndex(declarator, "(");
        if (paren >= 0 && paren < end)
            end = paren;
        return declarator[..end].Where(t => !t.Is("*")).ToList();
    }

    /// <summary>
    /// What a type is built on: an untagged struct or union body's key, "struct tag" or "union tag",
    /// a typedef name, or plain words such as "unsigned char" - storage and calling-convention words
    /// left out. Null for an enum, or nothing that names a type.
    /// </summary>
    private static string? BaseType(List<SourceToken> specifiers, Dictionary<(int, int), string> aggregateMarkers)
    {
        foreach (var token in specifiers)
            if (token.Is("{}") && aggregateMarkers.TryGetValue((token.Line, token.Column), out var key))
                return key;

        for (var i = 0; i + 1 < specifiers.Count; i++)
            if (specifiers[i].Text is "struct" or "union" && specifiers[i + 1].Kind == TokenKind.Identifier)
                return $"{specifiers[i].Text} {specifiers[i + 1].Text}";

        if (specifiers.Any(t => t.Is("enum")))
            return null;

        var words = specifiers.Where(t => t.Kind == TokenKind.Identifier && !Qualifiers.Contains(t.Text)).Select(t => t.Text).ToList();
        return words.Count > 0 ? string.Join(' ', words) : null;
    }

    /// <summary>How many pointer and array levels a declarator puts on its name: its stars, and its
    /// brackets after the name.</summary>
    private static int Indirection(List<SourceToken> declarator, SourceToken name)
    {
        var at = declarator.IndexOf(name);
        if (at < 0)
            return 0;
        var stars = declarator.Take(at).Count(t => t.Is("*"));
        var brackets = 0;
        for (var i = at + 1; i < declarator.Count && declarator[i].Text is "[" or "]" or ")" ; i++)
        {
            if (declarator[i].Is("["))
            {
                brackets++;
                i = MatchingClose(declarator, i);
            }
        }
        return stars + brackets;
    }

    /// <summary>A function's signature as written, from its return type to the end of its
    /// parameter list - storage-class words left out.</summary>
    private static CallSignature? Signature(List<SourceToken> statement, SourceToken name)
    {
        var at = statement.IndexOf(name);
        if (at < 0 || at + 1 >= statement.Count || !statement[at + 1].Is("("))
            return null;
        var close = MatchingClose(statement, at + 1);
        var start = 0;
        while (start < at && statement[start].Text is "static" or "extern" or "inline" or "typedef")
            start++;
        return BuildSignature(statement[start..Math.Min(close + 1, statement.Count)]);
    }

    /// <summary>Joins a declaration's tokens the way it would be written - "char *s", "f(a, b)" - and
    /// finds where each parameter sits between the first "(" and its ")".</summary>
    private static CallSignature BuildSignature(List<SourceToken> tokens)
    {
        var text = new System.Text.StringBuilder();
        string? previous = null;
        foreach (var token in tokens)
        {
            if (previous is not null && !(previous is "(" or "[" or "*" || token.Text is ")" or "]" or "," or "(" or "["))
                text.Append(' ');
            text.Append(token.Text);
            previous = token.Text;
        }

        var signature = text.ToString();
        var parameters = new List<(int, int)>();
        var open = signature.IndexOf('(');
        if (open >= 0)
        {
            var depth = 0;
            var start = open + 1;
            for (var i = open; i < signature.Length; i++)
            {
                var c = signature[i];
                if (c is '(' or '[')
                    depth++;
                else if (c is ')' or ']')
                    depth--;
                if ((c == ',' && depth == 1) || (c == ')' && depth == 0))
                {
                    var parameter = signature[start..i].Trim();
                    if (parameter.Length > 0 && parameter != "void")
                        parameters.Add((signature.IndexOf(parameter, start, StringComparison.Ordinal), parameter.Length));
                    start = i + 1;
                    if (c == ')')
                        break;
                }
            }
        }
        return new CallSignature(signature, parameters);
    }

    private static int TopLevelIndex(List<SourceToken> tokens, string text)
    {
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (depth == 0 && tokens[i].Is(text))
                return i;
            if (tokens[i].Text is "(" or "[")
                depth++;
            else if (tokens[i].Text is ")" or "]")
                depth = Math.Max(0, depth - 1);
        }
        return -1;
    }

    private static int MatchingClose(List<SourceToken> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Text is "(" or "[")
                depth++;
            else if (tokens[i].Text is ")" or "]" && --depth == 0)
                return i;
        }
        return tokens.Count;
    }

    private static IEnumerable<List<SourceToken>> SplitTopLevel(List<SourceToken> tokens, string separator)
    {
        var current = new List<SourceToken>();
        var depth = 0;
        foreach (var token in tokens)
        {
            if (depth == 0 && token.Is(separator))
            {
                yield return current;
                current = [];
                continue;
            }
            if (token.Text is "(" or "[")
                depth++;
            else if (token.Text is ")" or "]")
                depth = Math.Max(0, depth - 1);
            current.Add(token);
        }
        if (current.Count > 0)
            yield return current;
    }
}
