namespace Tedide.Build.Opt6502;

/// <summary>
/// One line of ca65 source as the optimizer sees it: an optional label, an opcode (an
/// instruction mnemonic or a directive such as <c>.segment</c>), its operand and a trailing
/// comment. The original text is kept, so a line no optimization changed is written back exactly
/// as it was read - see <see cref="ProgramText.Write"/>.
/// </summary>
internal sealed class AsmLine
{
    private AsmLine(string source, string? label, string? opcode, string? operand, string? comment)
    {
        Source = source;
        Label = label;
        Opcode = opcode;
        Operand = operand;
        Comment = comment;
        OriginalOpcode = opcode;
        OriginalOperand = operand;
    }

    public string Source { get; }

    /// <summary>The label's text without its colon - except a ca65 global label (<c>name::</c>),
    /// which keeps one of its two.</summary>
    public string? Label { get; set; }

    public string? Opcode { get; set; }
    public string? Operand { get; set; }
    public string? Comment { get; }
    public string? OriginalOpcode { get; }
    public string? OriginalOperand { get; }

    /// <summary>Removed by an optimization; written out as nothing (or its label, if it still has one).</summary>
    public bool IsDead { get; set; }

    /// <summary>Inside a <c>;#NOOPT</c> ... <c>;#OPT</c> region.</summary>
    public bool NoOptimize { get; init; }

    /// <summary>Between a label and a jump or branch back to it - see <see cref="RuntimeInliner"/>.</summary>
    public bool InLoop { get; set; }

    /// <summary>Added by <see cref="RuntimeInliner"/> rather than read from the source.</summary>
    public bool IsInserted { get; init; }

    /// <summary>Whether an optimization rewrote the opcode or operand, so the line has to be rebuilt.</summary>
    public bool IsChanged =>
        !string.Equals(Opcode, OriginalOpcode, StringComparison.Ordinal) ||
        !string.Equals(Operand, OriginalOperand, StringComparison.Ordinal);

    /// <summary>
    /// Parses one line by ca65's rules. A label starts in column 0 and ends with a colon - any
    /// other column-0 token (<c>.segment</c>, <c>.proc</c>, a <c>name = value</c> assignment) is an
    /// ordinary statement, and <c>name:=</c> is an assignment, not a label. A comment starts at the
    /// first <c>;</c> outside a quoted string, so <c>.byte "a;b"</c> keeps its operand whole.
    /// Blank and comment-only lines have no label or opcode.
    /// </summary>
    public static AsmLine Parse(string source, bool noOptimize = false, bool inserted = false)
    {
        var position = 0;
        string? label = null;

        if (source.Length > 0 && !IsSpace(source[0]) && source[0] != ';')
        {
            var end = 0;
            while (end < source.Length && !IsSpace(source[end]) && source[end] != ':' && source[end] != ';')
                end++;
            var hasColon = end < source.Length && source[end] == ':';
            var isAssignment = hasColon && end + 1 < source.Length && source[end + 1] == '=';
            if (end > 0 && hasColon && !isAssignment)
            {
                var labelEnd = end + 1 < source.Length && source[end + 1] == ':' ? end + 1 : end;
                label = source[..labelEnd];
                position = labelEnd + 1;
            }
        }

        while (position < source.Length && IsSpace(source[position]))
            position++;

        var commentStart = FindComment(source, position);

        var opcodeStart = position;
        while (position < commentStart && !IsSpace(source[position]))
            position++;
        string? opcode = position > opcodeStart ? source[opcodeStart..position] : null;

        while (position < commentStart && IsSpace(source[position]))
            position++;
        var operandEnd = commentStart;
        while (operandEnd > position && IsSpace(source[operandEnd - 1]))
            operandEnd--;
        string? operand = operandEnd > position ? source[position..operandEnd] : null;

        string? comment = commentStart < source.Length ? source[commentStart..] : null;

        return new AsmLine(source, label, opcode, operand, comment) { NoOptimize = noOptimize, IsInserted = inserted };
    }

    /// <summary>The index of the first <c>;</c> at or after <paramref name="start"/> that isn't
    /// inside a '...' or "..." literal, or the line's length if there's none.</summary>
    private static int FindComment(string source, int start)
    {
        var quote = '\0';
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == ';')
            {
                return i;
            }
        }
        return source.Length;
    }

    /// <summary>The C locale's whitespace, which is what assembly source uses - not Unicode's wider set.</summary>
    public static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';
}
