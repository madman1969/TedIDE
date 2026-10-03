using System.Text;
using static Tedide.Build.Opt6502.Instructions;

namespace Tedide.Build.Opt6502;

/// <summary>The kinds of optimization counted in <see cref="Opt6502Stats.ByKind"/>, with their keys.</summary>
internal enum OptimizationKind
{
    Reload,
    Constant,
    Transfer,
    Jump,
    Unreachable,
    Stz,
    Inline,
    Thread,
}

/// <summary>
/// A ca65 source file being optimized: its lines, the zero-page symbols it imports, and running
/// totals of what the passes did. Passes work on line indices; <see cref="RuntimeInliner"/> is
/// the only one that inserts lines.
/// </summary>
internal sealed class ProgramText
{
    private readonly int[] _byKind = new int[Enum.GetValues<OptimizationKind>().Length];

    private ProgramText(List<AsmLine> lines, HashSet<string> zeroPageSymbols)
    {
        Lines = lines;
        ZeroPageSymbols = zeroPageSymbols;
    }

    public List<AsmLine> Lines { get; }

    /// <summary>Every name a <c>.importzp</c>/<c>.exportzp</c>/<c>.globalzp</c> line declares.</summary>
    public HashSet<string> ZeroPageSymbols { get; }

    public int Optimizations { get; private set; }
    public int Removed { get; set; }
    public int Rewritten { get; set; }
    public int BytesSaved { get; set; }
    public int CyclesSaved { get; set; }

    /// <summary>A pass introduced a 65C02-only instruction (STZ), so <c>.setcpu "6502"</c> is
    /// raised to "65C02" on output.</summary>
    public bool UsesCmosInstructions { get; set; }

    /// <summary>
    /// Splits <paramref name="text"/> into lines - each ends at its newline, or at a carriage
    /// return (so CRLF and LF files read the same) - and parses them, tracking <c>;#NOOPT</c> /
    /// <c>;#OPT</c> regions (the directive's own line belongs to the region it starts or ends).
    /// </summary>
    public static ProgramText Parse(string text)
    {
        var lines = new List<AsmLine>();
        var zeroPage = new HashSet<string>(StringComparer.Ordinal);
        var optimize = true;

        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            var line = text[start..end];
            var carriageReturn = line.IndexOf('\r');
            if (carriageReturn >= 0)
                line = line[..carriageReturn];

            var trimmed = line.TrimStart(' ', '\t', '\n', '\v', '\f', '\r');
            if (trimmed.StartsWith(';'))
            {
                var directive = trimmed[1..].TrimStart(' ', '\t', '\n', '\v', '\f', '\r');
                if (directive.StartsWith("#NOOPT", StringComparison.Ordinal))
                    optimize = false;
                else if (directive.StartsWith("#OPT", StringComparison.Ordinal))
                    optimize = true;
            }

            var parsed = AsmLine.Parse(line, noOptimize: !optimize);
            CollectZeroPageSymbols(parsed, zeroPage);
            lines.Add(parsed);

            if (newline < 0)
                break;
            start = newline + 1;
        }

        return new ProgramText(lines, zeroPage);
    }

    private static void CollectZeroPageSymbols(AsmLine line, HashSet<string> zeroPage)
    {
        if (line.Operand is null || !(Is(line, ".importzp") || Is(line, ".exportzp") || Is(line, ".globalzp")))
            return;
        foreach (var name in line.Operand.Split([' ', '\t', '\n', '\v', '\f', '\r', ','], StringSplitOptions.RemoveEmptyEntries))
            zeroPage.Add(name);
    }

    /// <summary>The next live line after <paramref name="index"/> that isn't transparent (see
    /// <see cref="Instructions.IsTransparent"/>), or -1.</summary>
    public int NextSignificant(int index)
    {
        for (var i = index + 1; i < Lines.Count; i++)
        {
            if (!Lines[i].IsDead && !IsTransparent(Lines[i]))
                return i;
        }
        return -1;
    }

    /// <summary>The line carrying <paramref name="label"/> in the same <c>.proc</c> as line
    /// <paramref name="from"/> (or anywhere outside a <c>.proc</c>), or -1 - e.g. for an imported
    /// symbol such as a runtime helper.</summary>
    public int FindLabel(int from, string label)
    {
        var procStart = 0;
        for (var i = 0; i < from; i++)
        {
            if (IsProcBoundary(Lines[i], ".proc"))
                procStart = i;
            if (IsProcBoundary(Lines[i], ".endproc"))
                procStart = i + 1;
        }
        for (var i = procStart; i < Lines.Count; i++)
        {
            if (i != procStart && IsProcBoundary(Lines[i], ".proc"))
                break;
            if (Lines[i].Label == label)
                return i;
            if (IsProcBoundary(Lines[i], ".endproc"))
                break;
        }
        return -1;
    }

    /// <summary>The first instruction executed on reaching the label at <paramref name="labelIndex"/>,
    /// looking through transparent and label-only lines; -1 if that's a directive or the end.</summary>
    public int InstructionAt(int labelIndex)
    {
        for (var i = labelIndex; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if (line.IsDead || IsTransparent(line))
                continue;
            if (IsDirective(line))
                return -1;
            if (line.Opcode is not null)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// The estimated size and cycles of one run of <paramref name="line"/>'s instruction, from its
    /// addressing mode, ignoring page-crossing and branch-taken penalties. An operand is zero page
    /// when it's a literal below $100 or its leading symbol is in <see cref="ZeroPageSymbols"/>;
    /// anything else is taken to be absolute.
    /// </summary>
    public (int Bytes, int Cycles) EstimateCost(AsmLine line)
    {
        var operand = line.Operand;
        if (operand is null || operand.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            if (Is(line, "PHA") || Is(line, "PHP") || Is(line, "PHX") || Is(line, "PHY")) return (1, 3);
            if (Is(line, "PLA") || Is(line, "PLP") || Is(line, "PLX") || Is(line, "PLY")) return (1, 4);
            if (Is(line, "RTS") || Is(line, "RTI")) return (1, 6);
            if (Is(line, "BRK")) return (1, 7);
            return (1, 2);
        }
        if (operand[0] == '#' || IsShortBranch(line))
            return (2, 2);
        if (Is(line, "JSR"))
            return (3, 6);
        if (Is(line, "JMP"))
            return (3, operand[0] == '(' ? 5 : 3);
        if (operand[0] == '(')
            return (2, operand.Contains(",x)") || operand.Contains(",X)") ? 6 : 5);

        var indexed = operand.Contains(',');
        var zeroPage = IsZeroPage(operand);
        var bytes = zeroPage ? 2 : 3;
        if (IsReadModifyWrite(line))
            return (bytes, zeroPage ? (indexed ? 6 : 5) : (indexed ? 7 : 6));
        if (Is(line, "STA") && indexed && !zeroPage)
            return (bytes, 5);
        return (bytes, zeroPage ? (indexed ? 4 : 3) : 4);
    }

    private static readonly HashSet<string> ShortBranches = new(
        ["BCC", "BCS", "BEQ", "BNE", "BMI", "BPL", "BVC", "BVS", "BRA"], StringComparer.OrdinalIgnoreCase);

    public static bool IsShortBranch(AsmLine line) => line.Opcode is { } opcode && ShortBranches.Contains(opcode);

    private bool IsZeroPage(string operand)
    {
        if (operand[0] == '$')
        {
            var digits = 0;
            while (1 + digits < operand.Length && Uri.IsHexDigit(operand[1 + digits]))
                digits++;
            return digits is > 0 and <= 2;
        }
        if (char.IsAsciiDigit(operand[0]))
        {
            long value = 0;
            for (var i = 0; i < operand.Length && char.IsAsciiDigit(operand[i]) && value < 256; i++)
                value = value * 10 + (operand[i] - '0');
            return value < 256;
        }
        var length = 0;
        while (length < operand.Length && (char.IsAsciiLetterOrDigit(operand[length]) || operand[length] is '_' or '@' or '.'))
            length++;
        return length > 0 && ZeroPageSymbols.Contains(operand[..length]);
    }

    /// <summary>Marks line <paramref name="index"/> dead as an optimization of <paramref name="kind"/>,
    /// counting it and the bytes and cycles it saves.</summary>
    public void Remove(int index, OptimizationKind kind)
    {
        var (bytes, cycles) = EstimateCost(Lines[index]);
        Lines[index].IsDead = true;
        Optimizations++;
        _byKind[(int)kind]++;
        Removed++;
        BytesSaved += bytes;
        CyclesSaved += cycles;
    }

    /// <summary>Counts an in-place rewrite of <paramref name="kind"/>.</summary>
    public void RecordRewrite(OptimizationKind kind, int bytesSaved, int cyclesSaved)
    {
        Optimizations++;
        _byKind[(int)kind]++;
        Rewritten++;
        BytesSaved += bytesSaved;
        CyclesSaved += cyclesSaved;
    }

    /// <summary>Counts an inlined runtime call: the call is removed, and its body's size is
    /// subtracted from the bytes saved (usually leaving them negative).</summary>
    public void RecordInline(int bytesSaved, int cyclesSaved)
    {
        Optimizations++;
        _byKind[(int)OptimizationKind.Inline]++;
        Removed++;
        BytesSaved += bytesSaved;
        CyclesSaved += cyclesSaved;
    }

    public Opt6502Stats Stats() => new(
        Optimizations, Removed, Rewritten, BytesSaved, CyclesSaved,
        Enum.GetValues<OptimizationKind>().ToDictionary(KeyOf, kind => _byKind[(int)kind]));

    public static string KeyOf(OptimizationKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>
    /// The optimized source. A line no pass changed is written back exactly as read; a changed
    /// one is rebuilt as label, tab, opcode, five spaces, operand, tab, comment. A dead line writes
    /// nothing - or just its label, should it still carry one. The one deliberate change to an
    /// untouched line: once STZ has been introduced, <c>.setcpu "6502"</c> becomes "65C02" so ca65
    /// accepts it.
    /// </summary>
    public string Write(string newline)
    {
        var output = new StringBuilder();
        foreach (var line in Lines)
        {
            if (line.IsDead)
            {
                if (line.Label is not null)
                    output.Append(line.Label).Append(':').Append(newline);
                continue;
            }

            if (UsesCmosInstructions && Is(line, ".setcpu") &&
                string.Equals(line.Operand, "\"6502\"", StringComparison.OrdinalIgnoreCase))
            {
                output.Append("\t.setcpu\t\t\"65C02\"").Append(newline);
                continue;
            }

            if (!line.IsChanged)
            {
                output.Append(line.Source).Append(newline);
                continue;
            }

            if (line.Label is not null)
                output.Append(line.Label).Append(':');
            output.Append('\t').Append(line.Opcode);
            if (line.Operand is not null)
                output.Append("     ").Append(line.Operand);
            if (line.Comment is not null)
                output.Append('\t').Append(line.Comment);
            output.Append(newline);
        }
        return output.ToString();
    }
}
