namespace Tedide.Core.Navigation;

/// <summary>Which scanner a file's text goes through, decided by its extension.</summary>
public enum SourceLanguage
{
    Other,
    C,
    Assembly,
}

/// <summary>
/// Splits C and ca65 source into <see cref="SourceToken"/>s, dropping comments and whitespace, so
/// that a word inside a comment or a string is never mistaken for a use of a symbol. Deliberately
/// forgiving: an unterminated string or comment just runs to the end of its line (or file), and
/// anything unrecognised becomes punctuation, since code being edited is often half-written.
/// </summary>
public static class SourceTokenizer
{
    public static SourceLanguage LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".c" or ".h" => SourceLanguage.C,
        ".s" or ".asm" or ".inc" => SourceLanguage.Assembly,
        _ => SourceLanguage.Other,
    };

    public static List<SourceToken> Tokenize(string text, SourceLanguage language) => language switch
    {
        SourceLanguage.C => TokenizeC(text),
        SourceLanguage.Assembly => TokenizeAssembly(text),
        _ => [],
    };

    private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    public static List<SourceToken> TokenizeC(string text)
    {
        var tokens = new List<SourceToken>();
        var line = 1;
        var lineStart = 0;
        var directive = 0;
        var directiveCount = 0;
        // True from a line's start until its first token, so a '#' there opens a preprocessor line.
        var atLineStart = true;
        var i = 0;

        void NewLine(int newlineIndex)
        {
            // A directive continues onto the next line only when this one ends in a backslash.
            var continued = newlineIndex > 0 && (text[newlineIndex - 1] == '\\'
                || (text[newlineIndex - 1] == '\r' && newlineIndex > 1 && text[newlineIndex - 2] == '\\'));
            if (!continued)
                directive = 0;
            line++;
            lineStart = newlineIndex + 1;
            atLineStart = true;
        }

        void Add(TokenKind kind, int start, int end) =>
            tokens.Add(new SourceToken(kind, text[start..end], line, start - lineStart + 1, directive));

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                NewLine(i);
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c) || c == '\\')
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    // A block comment can span lines, but never ends a directive by itself.
                    if (text[i] == '\n')
                    {
                        line++;
                        lineStart = i + 1;
                    }
                    i++;
                }
                i = Math.Min(i + 2, text.Length);
                continue;
            }

            var wasLineStart = atLineStart;
            atLineStart = false;

            if (c == '#' && wasLineStart)
            {
                directive = ++directiveCount;
                Add(TokenKind.Punctuation, i, i + 1);
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                var start = i++;
                while (i < text.Length && text[i] != c && text[i] != '\n')
                    i += text[i] == '\\' && i + 1 < text.Length && text[i + 1] != '\n' ? 2 : 1;
                if (i < text.Length && text[i] == c)
                    i++;
                Add(TokenKind.String, start, i);
                continue;
            }

            // <stdio.h> straight after "#include" is a header name, not two comparisons.
            if (c == '<' && directive != 0 && tokens.Count >= 2 && tokens[^1].Is("include") && tokens[^2].Is("#"))
            {
                var end = text.IndexOfAny(['>', '\n'], i + 1);
                if (end >= 0 && text[end] == '>')
                {
                    Add(TokenKind.String, i, end + 1);
                    i = end + 1;
                    continue;
                }
            }

            if (IsIdentifierStart(c))
            {
                var start = i;
                while (i < text.Length && IsIdentifierPart(text[i]))
                    i++;
                Add(TokenKind.Identifier, start, i);
                continue;
            }
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                var start = i;
                while (i < text.Length && (IsIdentifierPart(text[i]) || text[i] == '.'))
                    i++;
                Add(TokenKind.Number, start, i);
                continue;
            }

            Add(TokenKind.Punctuation, i, i + 1);
            i++;
        }

        return tokens;
    }

    public static List<SourceToken> TokenizeAssembly(string text)
    {
        var tokens = new List<SourceToken>();
        var line = 1;
        var lineStart = 0;
        var i = 0;

        void Add(TokenKind kind, int start, int end) =>
            tokens.Add(new SourceToken(kind, text[start..end], line, start - lineStart + 1));

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                line++;
                lineStart = ++i;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == ';')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                var start = i++;
                while (i < text.Length && text[i] != c && text[i] != '\n')
                    i++;
                if (i < text.Length && text[i] == c)
                    i++;
                Add(TokenKind.String, start, i);
                continue;
            }

            // Symbols, including cheap locals (@loop) and control commands (.proc).
            if (IsIdentifierStart(c) || ((c == '@' || c == '.') && i + 1 < text.Length && IsIdentifierStart(text[i + 1])))
            {
                var start = i++;
                while (i < text.Length && IsIdentifierPart(text[i]))
                    i++;
                Add(c == '.' ? TokenKind.Directive : TokenKind.Identifier, start, i);
                continue;
            }

            // $FF and %1010 are numbers - without this, the "FF" would read as a symbol.
            if ((c == '$' && i + 1 < text.Length && char.IsAsciiHexDigit(text[i + 1]))
                || (c == '%' && i + 1 < text.Length && text[i + 1] is '0' or '1'))
            {
                var start = i++;
                while (i < text.Length && char.IsAsciiHexDigit(text[i]))
                    i++;
                Add(TokenKind.Number, start, i);
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < text.Length && IsIdentifierPart(text[i]))
                    i++;
                Add(TokenKind.Number, start, i);
                continue;
            }

            Add(TokenKind.Punctuation, i, i + 1);
            i++;
        }

        return tokens;
    }
}
