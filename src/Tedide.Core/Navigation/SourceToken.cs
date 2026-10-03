namespace Tedide.Core.Navigation;

/// <summary>What a <see cref="SourceToken"/> is. Comments are never tokens at all, so anything that
/// reaches a scanner is code.</summary>
public enum TokenKind
{
    /// <summary>A C identifier, or a ca65 symbol (including a cheap local such as <c>@loop</c>).</summary>
    Identifier,

    /// <summary>A ca65 control command such as <c>.proc</c> - the leading dot is part of the text.</summary>
    Directive,

    Number,

    /// <summary>A string or character literal, quotes included. A C <c>#include &lt;name&gt;</c>
    /// header name is one too, angle brackets included.</summary>
    String,

    /// <summary>Any other single character: an operator, bracket or separator.</summary>
    Punctuation,
}

/// <summary>
/// One token of a C or ca65 source file. <see cref="Line"/> and <see cref="Column"/> are 1-based and
/// count characters, the same way the editor's own line/offset model does. <see cref="Directive"/> is
/// the C preprocessor line the token belongs to (1 for the file's first <c>#</c> line, 2 for the
/// next, ...), or 0 for ordinary code - always 0 for ca65, which has no separate preprocessor.
/// </summary>
public readonly record struct SourceToken(TokenKind Kind, string Text, int Line, int Column, int Directive = 0)
{
    /// <summary>The 1-based column just past the token's last character.</summary>
    public int EndColumn => Column + Text.Length;

    public bool Is(string text) => Text == text;

    /// <summary>True if <paramref name="column"/> is inside the token, or straight after it - the
    /// caret sitting just past a word still means that word, as in Visual Studio.</summary>
    public bool Covers(int line, int column) => Line == line && column >= Column && column <= EndColumn;
}
