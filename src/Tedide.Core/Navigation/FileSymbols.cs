namespace Tedide.Core.Navigation;

/// <summary>
/// One file's symbols as <see cref="SymbolIndex"/> keeps them: its definitions (with C types and
/// signatures where there are any), the <c>#if</c> branches its target rules out, and the files its
/// <c>#include</c>/<c>.include</c> lines resolve to.
/// </summary>
public sealed class FileSymbols
{
    private FileSymbols(string path, SourceLanguage language, IReadOnlyList<SymbolDefinition> definitions,
        IReadOnlyDictionary<SymbolDefinition, SymbolDetail> details, IReadOnlyList<SourceScope> inactive,
        IReadOnlyList<string> includes, DateTime stamp, int generation)
    {
        Path = path;
        Language = language;
        Definitions = definitions;
        Details = details;
        Inactive = inactive;
        Includes = includes;
        Stamp = stamp;
        Generation = generation;
    }

    public string Path { get; }
    public SourceLanguage Language { get; }
    public IReadOnlyList<SymbolDefinition> Definitions { get; }
    public IReadOnlyDictionary<SymbolDefinition, SymbolDetail> Details { get; }

    /// <summary>Lines in <c>#if</c> branches the target's macros rule out.</summary>
    public IReadOnlyList<SourceScope> Inactive { get; }

    /// <summary>The files this one includes, resolved to full paths, in order - those in inactive
    /// branches, and those that can't be found, left out.</summary>
    public IReadOnlyList<string> Includes { get; }

    /// <summary>When the file on disk was last written, as of reading it - or
    /// <see cref="DateTime.MaxValue"/> for text taken from the editor, which the disk can't be newer than.</summary>
    public DateTime Stamp { get; }

    /// <summary>The <see cref="SymbolIndex"/> settings it was scanned with - see <see cref="SymbolIndex.Configure"/>.</summary>
    internal int Generation { get; }

    public bool IsActive(int line) => !Inactive.Any(range => range.Contains(line));

    public SymbolDetail? DetailOf(SymbolDefinition definition) => Details.GetValueOrDefault(definition);

    /// <summary>Scans <paramref name="text"/> as the contents of <paramref name="path"/>.</summary>
    /// <param name="resolveInclude">Finds the file an include names - (including file, name, whether
    /// it's a &lt;system&gt; include) - or null.</param>
    public static FileSymbols Scan(string path, string text, IReadOnlySet<string> macros,
        Func<string, string, bool, string?> resolveInclude, DateTime stamp, int generation = 0)
    {
        var language = SourceTokenizer.LanguageOf(path);
        var tokens = SourceTokenizer.Tokenize(text, language);
        CScan scan = language switch
        {
            SourceLanguage.C => CSymbolScanner.ScanDetailed(path, tokens),
            SourceLanguage.Assembly => AsmSymbolScanner.ScanDetailed(path, tokens),
            _ => new CScan([], []),
        };
        var inactive = language == SourceLanguage.C ? PreprocessorConditions.InactiveRanges(tokens, macros) : [];

        var includes = new List<string>();
        foreach (var line in tokens.GroupBy(t => t.Line))
        {
            if (inactive.Any(r => r.Contains(line.Key)) || IncludedName(language, line.ToList()) is not { } included)
                continue;
            if (resolveInclude(path, included.Name, included.IsSystem) is { } resolved
                && !includes.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                includes.Add(resolved);
        }

        return new FileSymbols(path, language, scan.Definitions, scan.Details, inactive, includes, stamp, generation);
    }

    /// <summary>The file named by a <c>#include</c> or <c>.include</c> line, if it is one.</summary>
    internal static (string Name, bool IsSystem)? IncludedName(SourceLanguage language, IReadOnlyList<SourceToken> line)
    {
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var isInclude = language == SourceLanguage.C
                ? line[i].Is("include") && i > 0 && line[i - 1].Is("#")
                : line[i].Kind == TokenKind.Directive && line[i].Text.Equals(".include", StringComparison.OrdinalIgnoreCase);
            var name = line[i + 1];
            if (isInclude && name.Kind == TokenKind.String && name.Text.Length > 2)
                return (name.Text[1..^1], name.Text[0] == '<');
        }
        return null;
    }
}
