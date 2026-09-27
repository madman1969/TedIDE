using System.Text.RegularExpressions;

namespace Tedide.Core.Debugging;

/// <summary>A C variable's declared type, as far as the debugger needs it: the text to show, its
/// size where the type alone says (null for a struct/typedef it can't size), and how to read it.</summary>
public sealed record CVariableType(string Text, int? Size, bool IsPointer, bool IsSigned, bool IsChar);

/// <summary>
/// Finds the declared types of a function's parameters and locals in its C source. cc65's debug
/// info records no types at all (every one is "00"), so without this a local's bytes could only
/// be shown as raw hex. A light reading of declaration statements, not a C parser: it covers
/// "type name", "type *name", comma lists and initializers - what cc65 programs declare locals
/// with - and returns nothing for a name it can't find, leaving the caller to fall back to hex.
/// </summary>
public static partial class CDeclarations
{
    /// <summary>
    /// The declared type of each of <paramref name="names"/> in <paramref name="functionName"/>
    /// in <paramref name="source"/>: parameters from its signature, locals from the declarations
    /// in its body up to <paramref name="uptoLine"/> (1-based) - the innermost (latest) wins.
    /// </summary>
    public static IReadOnlyDictionary<string, CVariableType> FindTypes(string source, string functionName, IEnumerable<string> names, int uptoLine)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, CVariableType>(StringComparer.Ordinal);
        var lines = source.Split('\n');

        var signatureIndex = Array.FindLastIndex(lines, Math.Min(uptoLine, lines.Length) - 1,
            l => Regex.IsMatch(l, $@"\b{Regex.Escape(functionName)}\s*\(") && !l.TrimEnd().EndsWith(';'));
        if (signatureIndex < 0)
            return found;

        // Parameters: the text between the signature's parentheses (possibly over several lines).
        var header = string.Join(" ", lines.Skip(signatureIndex).Take(10));
        var open = header.IndexOf('(', header.IndexOf(functionName, StringComparison.Ordinal));
        var close = open < 0 ? -1 : header.IndexOf(')', open);
        if (open >= 0 && close > open)
        {
            foreach (var parameter in header[(open + 1)..close].Split(','))
            {
                if (DeclaratorRegex().Match(parameter.Trim()) is { Success: true } p && wanted.Contains(p.Groups["name"].Value))
                    found[p.Groups["name"].Value] = TypeOf(p.Groups["type"].Value, p.Groups["ptr"].Value);
            }
        }

        // Locals: declaration statements in the body up to the stopped line (so the innermost of
        // two same-named locals wins), then - for any not declared yet, like a local whose
        // declaration is still ahead - on to the function's closing brace.
        for (var i = signatureIndex + 1; i < Math.Min(uptoLine, lines.Length); i++)
            AddDeclarations(lines[i], wanted, found, overwrite: true);
        for (var i = Math.Max(uptoLine, signatureIndex + 1); i < lines.Length && !lines[i].StartsWith('}'); i++)
            AddDeclarations(lines[i], wanted, found, overwrite: false);
        return found;
    }

    private static void AddDeclarations(string line, HashSet<string> wanted, Dictionary<string, CVariableType> found, bool overwrite)
    {
        foreach (Match statement in DeclarationRegex().Matches(line))
        {
            var type = statement.Groups["type"].Value;
            if (StatementKeywords.Contains(type.Split(' ')[0]))
                continue;
            foreach (var declarator in SplitTopLevel(statement.Groups["decls"].Value))
            {
                if (NameRegex().Match(declarator) is { Success: true } d && wanted.Contains(d.Groups["name"].Value)
                    && (overwrite || !found.ContainsKey(d.Groups["name"].Value)))
                    found[d.Groups["name"].Value] = TypeOf(type, d.Groups["ptr"].Value);
            }
        }
    }

    private static CVariableType TypeOf(string baseType, string pointer)
    {
        var words = Regex.Split(baseType.Trim(), @"\s+").Where(w => w is not ("const" or "volatile" or "register" or "static")).ToList();
        var text = string.Join(" ", words) + pointer;
        if (pointer.Length > 0)
            return new CVariableType(text, 2, IsPointer: true, IsSigned: false, IsChar: false);

        var isChar = words.Contains("char");
        // cc65's plain char is unsigned; plain int/short/long are signed.
        var isSigned = words.Contains("signed") || (!words.Contains("unsigned") && !isChar);
        int? size = isChar ? 1
            : words.Contains("long") ? 4
            : words.Any(w => w is "int" or "short" or "unsigned" or "signed" or "enum") ? 2
            : null;
        return new CVariableType(text, size, IsPointer: false, isSigned, isChar);
    }

    private static IEnumerable<string> SplitTopLevel(string declarators)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < declarators.Length; i++)
        {
            switch (declarators[i])
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0:
                    yield return declarators[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return declarators[start..];
    }

    private static readonly HashSet<string> StatementKeywords =
        ["return", "else", "case", "goto", "sizeof", "if", "while", "for", "do", "switch", "break", "continue", "default"];

    private const string TypeWords =
        @"(?:(?:const|volatile|register|static|signed|unsigned|struct|union|enum)\s+)*[A-Za-z_]\w*(?:\s+(?:int|long|char|short))*";

    /// <summary>"type [*]name" - a parameter, or one declarator's start.</summary>
    [GeneratedRegex(@"^(?<type>" + TypeWords + @")\s*(?<ptr>\*+)?\s*(?<name>[A-Za-z_]\w*)\s*(?:\[.*\])?$")]
    private static partial Regex DeclaratorRegex();

    /// <summary>"type decl, decl...;" - a declaration statement, declarators possibly initialized.</summary>
    [GeneratedRegex(@"(?:^|[;{])\s*(?<type>" + TypeWords + @")(?:\s+|\s*(?=\*))(?<decls>\**\s*[A-Za-z_]\w*[^;]*);")]
    private static partial Regex DeclarationRegex();

    [GeneratedRegex(@"^\s*(?<ptr>\*+)?\s*(?<name>[A-Za-z_]\w*)")]
    private static partial Regex NameRegex();
}
