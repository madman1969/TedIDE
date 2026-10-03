namespace Tedide.Build.Opt6502;

/// <summary>
/// What each 6502 instruction does to the accumulator and the N/Z flags, and which ones end
/// straight-line code - the rules every pass relies on to stay safe. Deliberately conservative:
/// a mnemonic not listed here (a 65816-only instruction, or a ca65 macro such as cc65's
/// longbranch <c>jeq</c>) counts as reading everything and as ending straight-line code, so no
/// pass ever optimizes across something it doesn't understand. Mnemonics match case-insensitively
/// (cc65 writes lowercase).
/// </summary>
internal static class Instructions
{
    private static readonly HashSet<string> BranchesAndJumps = Set(
        "BCC", "BCS", "BEQ", "BNE", "BMI", "BPL", "BVC", "BVS", "BRA", "JMP", "JSR", "RTS", "RTI", "BRK");

    private static readonly HashSet<string> OverwritesAccumulator = Set("LDA", "PLA", "TXA", "TYA");

    private static readonly HashSet<string> ReadsAccumulator = Set(
        "ADC", "SBC", "AND", "ORA", "EOR", "CMP", "BIT", "STA", "PHA", "TAX", "TAY", "JSR", "JMP", "RTS", "RTI", "BRK");

    private static readonly HashSet<string> ReadsNzFlags = Set(
        "BEQ", "BNE", "BMI", "BPL", "PHP", "JSR", "JMP", "RTS", "RTI", "BRK");

    private static readonly HashSet<string> WritesNzFlags = Set(
        "LDA", "LDX", "LDY", "TAX", "TAY", "TXA", "TYA", "TSX", "PLA", "INX", "INY", "DEX", "DEY", "INC", "DEC",
        "ASL", "LSR", "ROL", "ROR", "AND", "ORA", "EOR", "ADC", "SBC", "CMP", "CPX", "CPY", "BIT");

    private static readonly HashSet<string> PreservesAccumulatorAndNz = Set(
        "STA", "STX", "STY", "STZ", "CLC", "SEC", "CLI", "SEI", "CLD", "SED", "CLV", "NOP", "PHA", "PHX", "PHY", "TXS", "PHP");

    private static readonly HashSet<string> ReadModifyWrite = Set("ASL", "LSR", "ROL", "ROR", "INC", "DEC");

    private static HashSet<string> Set(params string[] mnemonics) => new(mnemonics, StringComparer.OrdinalIgnoreCase);

    private static bool In(AsmLine line, HashSet<string> set) => line.Opcode is { } opcode && set.Contains(opcode);

    private static bool IsKnown(AsmLine line) =>
        In(line, BranchesAndJumps) || In(line, WritesNzFlags) || In(line, PreservesAccumulatorAndNz) || In(line, ReadsAccumulator);

    /// <summary>ASL/LSR/ROL/ROR/INC/DEC with no operand (or "a") work on the accumulator.</summary>
    private static bool IsAccumulatorMode(AsmLine line) =>
        In(line, ReadModifyWrite) && (line.Operand is null || line.Operand.Equals("a", StringComparison.OrdinalIgnoreCase));

    public static bool Is(AsmLine line, string mnemonic) =>
        line.Opcode is { } opcode && opcode.Equals(mnemonic, StringComparison.OrdinalIgnoreCase);

    public static bool IsDirective(AsmLine line) => line.Opcode is { Length: > 0 } opcode && opcode[0] == '.';

    /// <summary>A line the passes look straight past: blank, comment-only, or a <c>.dbg</c>
    /// debug-info directive. Never a labelled line.</summary>
    public static bool IsTransparent(AsmLine line) => line.Label is null && (line.Opcode is null || Is(line, ".dbg"));

    /// <summary>A live, unlabelled, optimizable machine instruction - the only kind of line any
    /// pass may delete. Labels stay (something may reference them), and so do directives.</summary>
    public static bool IsRemovable(AsmLine line) =>
        !line.IsDead && !line.NoOptimize && line.Label is null && line.Opcode is not null && !IsDirective(line);

    /// <summary>A label, any branch, jump, call or return, any directive other than <c>.dbg</c>,
    /// an unknown mnemonic, or anything in a <c>#NOOPT</c> region.</summary>
    public static bool EndsBasicBlock(AsmLine line)
    {
        if (line.Label is not null || line.NoOptimize)
            return true;
        if (IsDirective(line))
            return !Is(line, ".dbg");
        if (line.Opcode is null)
            return false;
        return In(line, BranchesAndJumps) || !IsKnown(line);
    }

    /// <summary>Reads the accumulator, including JSR/JMP/RTS/RTI/BRK, which hand it on.</summary>
    public static bool ReadsA(AsmLine line) => In(line, ReadsAccumulator) || IsAccumulatorMode(line) || !IsKnown(line);

    /// <summary>Replaces the accumulator without reading it.</summary>
    public static bool OverwritesA(AsmLine line) => In(line, OverwritesAccumulator);

    public static bool ReadsNz(AsmLine line) => In(line, ReadsNzFlags) || !IsKnown(line);

    /// <summary>Sets both N and Z without reading them.</summary>
    public static bool WritesNz(AsmLine line) => In(line, WritesNzFlags);

    /// <summary>Changes neither A nor N/Z: stores, flag set/clear, pushes, NOP.</summary>
    public static bool PreservesAAndNz(AsmLine line) => In(line, PreservesAccumulatorAndNz);

    public static bool IsReadModifyWrite(AsmLine line) => In(line, ReadModifyWrite);

    /// <summary>Indirect, or indexed by Y - addressing modes STZ doesn't have.</summary>
    public static bool IsIndirectOrYIndexed(string? operand)
    {
        if (operand is null)
            return false;
        if (operand.Contains('(') || operand.Contains('['))
            return true;
        var trimmed = operand.TrimEnd(' ', '\t', '\n', '\v', '\f', '\r');
        return trimmed.Length >= 2 && trimmed[^2] == ',' && char.ToLowerInvariant(trimmed[^1]) == 'y';
    }

    /// <summary>cc65 wraps each function in <c>.proc</c> / <c>.endproc</c> and reuses label
    /// names (L0001...) in every one, so label lookups never cross one.</summary>
    public static bool IsProcBoundary(AsmLine line, string directive) =>
        line.Label is { } label ? label.Equals(directive, StringComparison.OrdinalIgnoreCase) : Is(line, directive);
}
