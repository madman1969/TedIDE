namespace Tedide.Core.Navigation;

/// <summary>One use of a symbol found by <see cref="CodeNavigator.FindReferences"/>.</summary>
public sealed record SymbolReference(string FilePath, int Line, int Column, int Length, string LineText, bool IsDefinition);

/// <summary>The outcome of <see cref="CodeNavigator.GoToDefinition"/>. <see cref="Message"/> explains
/// an empty result (or notes something about a non-empty one) for the status/output line.</summary>
public sealed record DefinitionResult(string? Symbol, IReadOnlyList<SymbolDefinition> Definitions, string? Message = null);

/// <summary>The outcome of <see cref="CodeNavigator.FindReferences"/>.</summary>
public sealed record ReferencesResult(string? Symbol, IReadOnlyList<SymbolReference> References, string? Message = null);

/// <summary>One replacement of <see cref="OldText"/> by <see cref="NewText"/> at a 1-based line and column.</summary>
public sealed record TextEdit(string FilePath, int Line, int Column, string OldText, string NewText);

/// <summary>
/// The outcome of <see cref="CodeNavigator.PlanRename"/>: every edit a rename makes, or an
/// <see cref="Error"/> saying why it can't be done (in which case there are no edits).
/// </summary>
public sealed record RenamePlan(string? Symbol, IReadOnlyList<TextEdit> Edits, string? Error = null)
{
    public int FileCount => Edits.Select(e => e.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>
    /// Applies <paramref name="edits"/> (all for the same file) to <paramref name="text"/>, last
    /// first so earlier positions stay valid. Throws <see cref="InvalidDataException"/> if the text
    /// at an edit's position isn't what the edit expects - the file changed since the plan was made.
    /// </summary>
    public static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                lineStarts.Add(i + 1);

        var result = new System.Text.StringBuilder(text);
        foreach (var (edit, offset) in edits
            .Select(e => (Edit: e, Offset: e.Line - 1 < lineStarts.Count ? lineStarts[e.Line - 1] + e.Column - 1 : -1))
            .OrderByDescending(x => x.Offset))
        {
            if (offset < 0 || offset + edit.OldText.Length > text.Length
                || string.CompareOrdinal(text, offset, edit.OldText, 0, edit.OldText.Length) != 0)
                throw new InvalidDataException($"{Path.GetFileName(edit.FilePath)} has changed since the rename was worked out (line {edit.Line}).");
            result.Remove(offset, edit.OldText.Length).Insert(offset, edit.NewText);
        }
        return result.ToString();
    }
}

/// <summary>
/// Go To Definition and Find All References over a project's C and ca65 sources, built on
/// <see cref="CSymbolScanner"/> and <see cref="AsmSymbolScanner"/>. Everything is worked out fresh
/// from the files on each call - a cc65 project is a few dozen files at most, so there's no index
/// to keep in step with edits.
/// <para>
/// cc65 gives a C name an underscore in assembly, so a C <c>foo</c> and an assembly <c>_foo</c> are
/// the same symbol here, in both directions - a prototype in a header leads to the routine in a .s
/// file that implements it, and vice versa.
/// </para>
/// </summary>
public sealed class CodeNavigator
{
    private readonly IReadOnlyList<string> _projectFiles;
    private readonly Func<string, string?> _readText;
    private readonly IReadOnlyList<string> _includeDirectories;
    private readonly IReadOnlyList<string> _libraryDirectories;
    private readonly HashSet<string> _predefinedMacros;
    private readonly Dictionary<string, ParsedFile?> _parsed = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ParsedFile(
        string Path, SourceLanguage Language, string[] Lines, List<SourceToken> Tokens, List<SymbolDefinition> Definitions, List<SourceScope> Inactive)
    {
        public bool IsActive(int line) => !Inactive.Any(range => range.Contains(line));
    }

    /// <param name="projectFiles">Every source file the search covers, as absolute paths.</param>
    /// <param name="readText">Returns a file's current text, or null if it can't be read. The host
    /// passes the editor's unsaved text for the open file, so positions match what's on screen.</param>
    /// <param name="includeDirectories">The project's own include directories (absolute), for
    /// resolving an <c>#include</c>.</param>
    /// <param name="libraryDirectories">cc65's include and asminc directories: searched for an
    /// <c>#include</c>, and for a definition the project itself doesn't have (e.g. a conio.h
    /// function). Never searched for references.</param>
    /// <param name="predefinedMacros">Macros known to be defined - the target's own (see
    /// <see cref="Cc65TargetExtensions.PredefinedMacros"/>) and the project's -D defines - so a
    /// definition in an inactive <c>#if</c> branch is only offered when there's nothing else.</param>
    public CodeNavigator(
        IReadOnlyList<string> projectFiles,
        Func<string, string?> readText,
        IReadOnlyList<string>? includeDirectories = null,
        IReadOnlyList<string>? libraryDirectories = null,
        IEnumerable<string>? predefinedMacros = null)
    {
        _projectFiles = projectFiles;
        _readText = readText;
        _includeDirectories = includeDirectories ?? [];
        _libraryDirectories = libraryDirectories ?? [];
        _predefinedMacros = new HashSet<string>(predefinedMacros ?? [], StringComparer.Ordinal);
    }

    /// <summary>
    /// cc65's own include and asminc directories, for <c>libraryDirectories</c>: under
    /// <paramref name="cc65Home"/> (CC65_HOME) if that's set, otherwise beside the bin directory of
    /// the first cl65 on <paramref name="searchPath"/> (PATH) - the same two places cl65 itself
    /// relies on. Only directories that exist are returned.
    /// </summary>
    public static IReadOnlyList<string> Cc65LibraryDirectories(string? cc65Home, string? searchPath)
    {
        var home = !string.IsNullOrWhiteSpace(cc65Home) && Directory.Exists(cc65Home) ? cc65Home : null;
        if (home is null && !string.IsNullOrWhiteSpace(searchPath))
        {
            var cl65 = searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(directory => new[] { Path.Combine(directory, "cl65.exe"), Path.Combine(directory, "cl65") })
                .FirstOrDefault(File.Exists);
            if (cl65 is not null)
                home = Path.GetDirectoryName(Path.GetDirectoryName(cl65));
        }
        if (home is null)
            return [];

        return new[] { "include", "asminc" }
            .Select(name => Path.Combine(home, name))
            .Where(Directory.Exists)
            .ToList();
    }

    /// <summary>Reads a file from disk the way Find in Files does, or null if it can't be.</summary>
    public static string? ReadFromDisk(string path)
    {
        try
        {
            return File.Exists(path) ? SourceFileText.Read(path).Text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public DefinitionResult GoToDefinition(string file, int line, int column)
    {
        var parsed = Parse(file);
        if (parsed is null || parsed.Language == SourceLanguage.Other)
            return new DefinitionResult(null, [], "Go To Definition works in C and assembly files.");

        if (IncludedFileName(parsed, line) is { } included)
        {
            var resolved = ResolveInclude(parsed, included);
            return resolved is null
                ? new DefinitionResult(included.Name, [], $"Couldn't find {included.Name} in the project's include paths or cc65's.")
                : new DefinitionResult(included.Name, [new SymbolDefinition(Path.GetFileName(resolved), SymbolKind.File, resolved, 1, 1)]);
        }

        if (SymbolAt(parsed, line, column) is not { } token)
            return new DefinitionResult(null, [], "No symbol at the cursor.");

        if (LocalDefinition(parsed, token) is { } local)
            return new DefinitionResult(token.Text, [local]);

        var key = LinkKey(parsed.Language, token.Text);
        var isMemberAccess = IsMemberAccess(parsed, token);
        var candidates = GlobalDefinitions(_projectFiles, key).ToList();
        if (candidates.Count == 0)
        {
            // cc65's own headers: first those this file actually includes, directly or through
            // other headers, then any of them (the #include may simply not have been written yet).
            candidates = GlobalDefinitions(IncludedLibraryFiles(parsed), key).ToList();
            if (candidates.Count == 0)
                candidates = GlobalDefinitions(LibraryFiles(), key).ToList();
        }
        // A definition in a branch the target's #if rules out is only worth offering if it's all there is.
        if (candidates.Any(IsActive))
            candidates = candidates.Where(IsActive).ToList();
        if (isMemberAccess)
            candidates = candidates.Where(d => d.Kind == SymbolKind.Member).ToList();
        if (candidates.Count == 0)
            return new DefinitionResult(token.Text, [], $"No definition found for '{token.Text}'.");

        // Already on the definition: offer what else there is - typically the header's prototype -
        // so F12 flips between a function and its declaration.
        var self = candidates.FirstOrDefault(d => SamePlace(d, file, token));
        if (self is not null)
        {
            var others = candidates.Where(d => d != self).ToList();
            var declarations = others.Where(d => d.IsDeclaration).ToList();
            var pick = declarations.Count > 0 ? declarations : others;
            return pick.Count > 0
                ? new DefinitionResult(token.Text, Sort(pick, file))
                : new DefinitionResult(token.Text, [self], $"This is the definition of '{token.Text}'.");
        }

        var definitions = candidates.Where(d => !d.IsDeclaration).ToList();
        if (definitions.Any(d => d.Kind != SymbolKind.Member))
            definitions = definitions.Where(d => d.Kind != SymbolKind.Member).ToList();
        return new DefinitionResult(token.Text, Sort(definitions.Count > 0 ? definitions : candidates, file));
    }

    public ReferencesResult FindReferences(string file, int line, int column)
    {
        var parsed = Parse(file);
        if (parsed is null || parsed.Language == SourceLanguage.Other)
            return new ReferencesResult(null, [], "Find All References works in C and assembly files.");

        if (SymbolAt(parsed, line, column) is not { } token)
            return new ReferencesResult(null, [], "No symbol at the cursor.");

        var references = new List<SymbolReference>();
        if (LocalDefinition(parsed, token) is { Scope: { } scope } local)
        {
            foreach (var use in parsed.Tokens.Where(t => t.Kind == TokenKind.Identifier && t.Text == token.Text && scope.Contains(t.Line)))
                if (InnermostLocal(parsed, use) == local)
                    references.Add(Reference(parsed, use));
            return new ReferencesResult(token.Text, references);
        }

        var key = LinkKey(parsed.Language, token.Text);
        var wantMembers = IsMemberAccess(parsed, token)
            || parsed.Definitions.Any(d => d.Kind == SymbolKind.Member && SamePlace(d, file, token));
        foreach (var path in _projectFiles)
        {
            if (Parse(path) is not { } other)
                continue;
            foreach (var use in other.Tokens)
            {
                if (use.Kind != TokenKind.Identifier || LinkKey(other.Language, use.Text) != key)
                    continue;
                // A member and a variable can share a name; "p->x" (or x's own line in a struct body)
                // is only ever the member.
                var isMemberUse = IsMemberAccess(other, use)
                    || other.Definitions.Any(d => d.Kind == SymbolKind.Member && SamePlace(d, path, use));
                if (other.Language == SourceLanguage.C && isMemberUse != wantMembers)
                    continue;
                // A local of the same name hides the symbol inside its own scope.
                if (InnermostLocal(other, use) is not null)
                    continue;
                references.Add(Reference(other, use));
            }
        }
        return new ReferencesResult(token.Text, references);
    }

    /// <summary>
    /// Works out renaming the symbol at the caret to <paramref name="newName"/>: every reference
    /// <see cref="FindReferences"/> finds is rewritten, so comments, strings and same-named locals
    /// are left alone. <paramref name="newName"/> is spelled as in the file the caret is in, so
    /// renaming assembly's <c>_foo</c> takes an underscored name too; the C side of the link gets
    /// it without the underscore, and vice versa. Refused for a symbol the project doesn't define
    /// (a cc65 library function), an invalid name, or a name already in use.
    /// </summary>
    public RenamePlan PlanRename(string file, int line, int column, string newName)
    {
        newName = newName.Trim();
        var references = FindReferences(file, line, column);
        if (references.Symbol is not { } symbol)
            return new RenamePlan(null, [], references.Message ?? "No symbol at the cursor.");

        var parsed = Parse(file)!;
        var key = LinkKey(parsed.Language, symbol);
        if (newName == symbol)
            return new RenamePlan(symbol, [], "That's the name it already has.");
        if (!references.References.Any(r => r.IsDefinition))
            return new RenamePlan(symbol, [], $"'{symbol}' isn't defined in this project (it may come from cc65's own headers), so it can't be renamed here.");

        // The new name, as C would spell it, when the symbol is shared with C.
        var isCheapLocal = symbol.StartsWith('@');
        var isLinked = key.StartsWith("c:", StringComparison.Ordinal);
        string? error = null;
        if (isCheapLocal != newName.StartsWith('@'))
            error = isCheapLocal ? "A cheap local label's name must start with '@'." : "Only a cheap local label's name can start with '@'.";
        else if (!IsIdentifier(isCheapLocal ? newName[1..] : newName))
            error = $"'{newName}' isn't a valid name: use letters, digits and '_', not starting with a digit.";
        else if (isLinked && parsed.Language == SourceLanguage.Assembly && !newName.StartsWith('_'))
            error = $"'{symbol}' is shared with C code, which sees it without its leading '_' - the new name needs one too.";
        if (error is not null)
            return new RenamePlan(symbol, [], error);

        var cName = isLinked && parsed.Language == SourceLanguage.Assembly ? newName[1..] : newName;
        var newKey = LinkKey(parsed.Language, newName);
        var touchesC = references.References.Any(r => SourceTokenizer.LanguageOf(r.FilePath) == SourceLanguage.C);
        if (touchesC && CSymbolScanner.IsKeyword(cName))
            return new RenamePlan(symbol, [], $"'{cName}' is a C keyword.");

        if (FindConflict(parsed, line, column, newName, newKey) is { } conflict)
            return new RenamePlan(symbol, [],
                $"'{newName}' is already used - {conflict.KindText} at {Path.GetFileName(conflict.FilePath)}({conflict.Line}).");

        var edits = references.References.Select(r =>
        {
            var oldText = r.LineText.Substring(r.Column - 1, r.Length);
            var replacement = SourceTokenizer.LanguageOf(r.FilePath) == SourceLanguage.Assembly && isLinked ? "_" + cName
                : SourceTokenizer.LanguageOf(r.FilePath) == SourceLanguage.C ? cName
                : newName;
            return new TextEdit(r.FilePath, r.Line, r.Column, oldText, replacement);
        }).ToList();
        return new RenamePlan(symbol, edits);
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>An existing symbol the new name would collide with: for a local, another local of
    /// that name in the same file whose scope overlaps; for anything else, a project-wide symbol of
    /// that name, or a local of that name that would capture one of the references.</summary>
    private SymbolDefinition? FindConflict(ParsedFile file, int line, int column, string newName, string newKey)
    {
        var token = SymbolAt(file, line, column)!.Value;
        if (LocalDefinition(file, token) is { Scope: { } scope })
            return file.Definitions.FirstOrDefault(d => d.Name == newName && d.Scope is { } other
                && other.StartLine <= scope.EndLine && scope.StartLine <= other.EndLine);

        var global = GlobalDefinitions(_projectFiles, newKey).FirstOrDefault();
        if (global is not null)
            return global;

        var references = FindReferences(file.Path, line, column).References;
        foreach (var reference in references)
            if (Parse(reference.FilePath) is { } other)
                if (other.Definitions.FirstOrDefault(d => d.Scope is { } s && s.Contains(reference.Line)
                    && LinkKey(other.Language, d.Name) == newKey) is { } captured)
                    return captured;
        return null;
    }

    private SymbolReference Reference(ParsedFile file, SourceToken token)
    {
        var isDefinition = file.Definitions.Any(d => SamePlace(d, file.Path, token));
        var text = token.Line - 1 < file.Lines.Length ? file.Lines[token.Line - 1] : string.Empty;
        return new SymbolReference(file.Path, token.Line, token.Column, token.Text.Length, text, isDefinition);
    }

    private static bool SamePlace(SymbolDefinition definition, string path, SourceToken token) =>
        definition.Line == token.Line && definition.Column == token.Column
        && string.Equals(definition.FilePath, path, StringComparison.OrdinalIgnoreCase);

    private static SourceToken? SymbolAt(ParsedFile file, int line, int column)
    {
        // Prefer a word the caret is inside over one it merely sits just after ("a|+b" is a; "a+|b" is b).
        var onLine = file.Tokens.Where(t => t.Kind == TokenKind.Identifier && t.Covers(line, column)).ToList();
        var token = onLine.FirstOrDefault(t => column < t.EndColumn);
        if (token == default)
            token = onLine.FirstOrDefault();
        if (token == default)
            return null;
        if (file.Language == SourceLanguage.C && CSymbolScanner.IsKeyword(token.Text))
            return null;
        return token;
    }

    /// <summary>The innermost local (parameter, local variable, cheap local label) named like
    /// <paramref name="token"/> whose scope covers it, if any.</summary>
    private static SymbolDefinition? LocalDefinition(ParsedFile file, SourceToken token) =>
        IsMemberAccess(file, token) ? null : InnermostLocal(file, token);

    private static SymbolDefinition? InnermostLocal(ParsedFile file, SourceToken token) =>
        file.Definitions
            .Where(d => d.Scope is { } scope && d.Name == token.Text && scope.Contains(token.Line) && d.Line <= token.Line)
            .OrderByDescending(d => d.Scope!.Value.StartLine)
            .FirstOrDefault();

    /// <summary>Whether a C token follows "." or "->", i.e. names a struct/union member.</summary>
    private static bool IsMemberAccess(ParsedFile file, SourceToken token)
    {
        if (file.Language != SourceLanguage.C)
            return false;
        var index = file.Tokens.IndexOf(token);
        if (index < 1)
            return false;
        var previous = file.Tokens[index - 1];
        return previous.Is(".") || (previous.Is(">") && index >= 2 && file.Tokens[index - 2].Is("-")
            && file.Tokens[index - 2].EndColumn == previous.Column);
    }

    /// <summary>cc65 prefixes a C symbol with "_" in assembly, so both map to the same key; an
    /// assembly symbol without the prefix lives in a namespace of its own.</summary>
    private static string LinkKey(SourceLanguage language, string name) =>
        language == SourceLanguage.Assembly
            ? name.Length > 1 && name[0] == '_' ? "c:" + name[1..] : "asm:" + name
            : "c:" + name;

    private IEnumerable<SymbolDefinition> GlobalDefinitions(IEnumerable<string> files, string key)
    {
        foreach (var path in files)
            if (Parse(path) is { } file)
                foreach (var definition in file.Definitions)
                    if (!definition.IsLocal && LinkKey(file.Language, definition.Name) == key)
                        yield return definition;
    }

    private bool IsActive(SymbolDefinition definition) => Parse(definition.FilePath)?.IsActive(definition.Line) ?? true;

    /// <summary>The cc65 library headers reachable from <paramref name="start"/> through active
    /// <c>#include</c>/<c>.include</c> lines, following project headers along the way.</summary>
    private IEnumerable<string> IncludedLibraryFiles(ParsedFile start)
    {
        var library = new HashSet<string>(LibraryFiles(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.Path };
        var pending = new Queue<ParsedFile>([start]);
        while (pending.Count > 0)
        {
            var file = pending.Dequeue();
            foreach (var line in file.Tokens.Where(t => t.Is("include") || t.Text.Equals(".include", StringComparison.OrdinalIgnoreCase)).Select(t => t.Line).Distinct())
            {
                if (!file.IsActive(line) || IncludedFileName(file, line) is not { } included || ResolveInclude(file, included) is not { } path || !seen.Add(path))
                    continue;
                if (library.Contains(path))
                    yield return path;
                if (Parse(path) is { } next)
                    pending.Enqueue(next);
            }
        }
    }

    private static List<SymbolDefinition> Sort(IEnumerable<SymbolDefinition> definitions, string currentFile) =>
        definitions
            .OrderBy(d => string.Equals(d.FilePath, currentFile, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Line)
            .ToList();

    private IEnumerable<string> LibraryFiles()
    {
        foreach (var directory in _libraryDirectories)
        {
            if (!Directory.Exists(directory))
                continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
                if (SourceTokenizer.LanguageOf(path) != SourceLanguage.Other)
                    yield return path;
        }
    }

    private sealed record IncludedFile(string Name, bool IsSystem);

    /// <summary>The file named by a <c>#include</c> or <c>.include</c> on this line, if there is one.</summary>
    private static IncludedFile? IncludedFileName(ParsedFile file, int line)
    {
        var tokens = file.Tokens.Where(t => t.Line == line).ToList();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var isInclude = file.Language == SourceLanguage.C
                ? tokens[i].Is("include") && i > 0 && tokens[i - 1].Is("#")
                : tokens[i].Kind == TokenKind.Directive && tokens[i].Text.Equals(".include", StringComparison.OrdinalIgnoreCase);
            var name = tokens[i + 1];
            if (isInclude && name.Kind == TokenKind.String && name.Text.Length > 2)
                return new IncludedFile(name.Text[1..^1], name.Text[0] == '<');
        }
        return null;
    }

    private string? ResolveInclude(ParsedFile from, IncludedFile included)
    {
        var currentDirectory = Path.GetDirectoryName(from.Path) ?? string.Empty;
        IEnumerable<string> searched = included.IsSystem
            ? [.. _includeDirectories, .. _libraryDirectories, currentDirectory]
            : [currentDirectory, .. _includeDirectories, .. _libraryDirectories];
        return searched
            .Select(directory => Path.GetFullPath(Path.Combine(directory, included.Name)))
            .FirstOrDefault(path => File.Exists(path) || _projectFiles.Contains(path, StringComparer.OrdinalIgnoreCase));
    }

    private ParsedFile? Parse(string path)
    {
        if (_parsed.TryGetValue(path, out var cached))
            return cached;

        ParsedFile? parsed = null;
        var language = SourceTokenizer.LanguageOf(path);
        if (_readText(path) is { } text)
        {
            var tokens = SourceTokenizer.Tokenize(text, language);
            var definitions = language switch
            {
                SourceLanguage.C => CSymbolScanner.Scan(path, tokens),
                SourceLanguage.Assembly => AsmSymbolScanner.Scan(path, tokens),
                _ => [],
            };
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            var inactive = language == SourceLanguage.C ? PreprocessorConditions.InactiveRanges(tokens, _predefinedMacros) : [];
            parsed = new ParsedFile(path, language, lines, tokens, definitions, inactive);
        }
        _parsed[path] = parsed;
        return parsed;
    }
}
