using static Tedide.Build.Opt6502.Instructions;

namespace Tedide.Build.Opt6502;

/// <summary>
/// The size passes - each one only ever removes an instruction or rewrites one in place without
/// growing it, and only where the instruction rules (<see cref="Instructions"/>) prove the program
/// still does exactly the same thing. They run repeatedly until none finds anything more (see
/// <see cref="Opt6502Optimizer"/>), since one pass's removal can expose another's pattern.
/// </summary>
internal static class Passes
{
    /// <summary>
    /// <c>LDA x / STA y / LDA x</c>: STA changes neither A nor the flags, so the second load is
    /// redundant. Only when x is immediate or a plain symbol - never indirect (the STA could have
    /// rewritten the pointer) or a literal address (possibly an I/O register, where every read
    /// counts) - and neither the STA nor the second LDA carries a label.
    /// </summary>
    public static void RedundantReloads(ProgramText program)
    {
        var lines = program.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            var load = lines[i];
            if (load.IsDead || load.NoOptimize || !Is(load, "LDA") || load.Operand is not { } x)
                continue;
            if (x[0] != '#' && (IsIndirectOrYIndexed(x) || x[0] is '$' or '%' || char.IsAsciiDigit(x[0])))
                continue;

            var storeIndex = program.NextSignificant(i);
            if (storeIndex < 0 || lines[storeIndex] is not { Label: null, NoOptimize: false } store || !Is(store, "STA"))
                continue;

            var reloadIndex = program.NextSignificant(storeIndex);
            if (reloadIndex < 0 || !IsRemovable(lines[reloadIndex]) || !Is(lines[reloadIndex], "LDA") ||
                lines[reloadIndex].Operand != x)
                continue;

            program.Remove(reloadIndex, OptimizationKind.Reload);
        }
    }

    private static readonly string[] ImmediateLoads = ["LDA", "LDX", "LDY"];

    /// <summary>
    /// After <c>LDA/LDX/LDY #v</c>, a later load of the same register with the same <c>#v</c> is
    /// redundant while everything in between is straight-line code that changes neither the
    /// register nor N/Z (stores, flag set/clear, pushes, NOP).
    /// </summary>
    public static void RepeatedConstantLoads(ProgramText program)
    {
        var lines = program.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            var first = lines[i];
            if (first.IsDead || first.NoOptimize || first.Operand is not ['#', ..])
                continue;
            var register = ImmediateLoads.FirstOrDefault(load => Is(first, load));
            if (register is null)
                continue;

            for (var j = program.NextSignificant(i); j >= 0; j = program.NextSignificant(j))
            {
                var current = lines[j];
                if (Is(current, register) && IsRemovable(current) && current.Operand == first.Operand)
                {
                    program.Remove(j, OptimizationKind.Constant);
                    continue;
                }
                if (EndsBasicBlock(current) || !PreservesAAndNz(current))
                    break;
            }
        }
    }

    /// <summary>
    /// <c>TAX / TXA</c> (or <c>TAY / TYA</c>): after the first, A and the index register already
    /// match and N/Z reflect that value, so the transfer back is removed. The first stays - the
    /// index register still needs it.
    /// </summary>
    public static void RedundantTransfers(ProgramText program)
    {
        var lines = program.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.IsDead || line.NoOptimize)
                continue;
            var back = Is(line, "TAX") ? "TXA" : Is(line, "TAY") ? "TYA" : null;
            if (back is null)
                continue;

            var next = program.NextSignificant(i);
            if (next >= 0 && IsRemovable(lines[next]) && Is(lines[next], back))
                program.Remove(next, OptimizationKind.Transfer);
        }
    }

    /// <summary>
    /// Removes unlabelled instructions after an unconditional JMP, RTS or RTI: nothing can reach
    /// them, since every way into code is a label. Stops at the next label or directive, and looks
    /// through blank, comment and <c>.dbg</c> lines (which stay).
    /// </summary>
    public static void UnreachableCode(ProgramText program)
    {
        var lines = program.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.IsDead || !(Is(line, "JMP") || Is(line, "RTS") || Is(line, "RTI")))
                continue;

            for (var j = i + 1; j < lines.Count; j++)
            {
                var current = lines[j];
                if (current.IsDead || IsTransparent(current))
                    continue;
                if (current.Label is not null || IsDirective(current) || !IsRemovable(current))
                    break;
                program.Remove(j, OptimizationKind.Unreachable);
            }
        }
    }

    private const int MaxStoresPerZero = 64;

    /// <summary>
    /// 65C02 (and the SuperCPU's 65816): <c>LDA #0 / STA a / STA b ...</c> becomes
    /// <c>STZ a / STZ b ...</c> with the LDA removed - but only where every STA uses a mode STZ
    /// has (no indirect or Y-indexed stores), and the zero in A and the N/Z flags the LDA set are
    /// provably overwritten before anything reads them, all within straight-line code.
    /// </summary>
    public static void StoreZero(ProgramText program, bool cmosCpu)
    {
        if (!cmosCpu)
            return;
        var lines = program.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            var load = lines[i];
            if (!IsRemovable(load) || !Is(load, "LDA") || load.Operand is not ("#0" or "#$0" or "#$00"))
                continue;

            var stores = new List<int>();
            bool nzDead = false, aDead = false;
            for (var j = program.NextSignificant(i); j >= 0; j = program.NextSignificant(j))
            {
                var current = lines[j];
                if (EndsBasicBlock(current))
                    break;
                if (Is(current, "STA"))
                {
                    if (!IsRemovable(current) || IsIndirectOrYIndexed(current.Operand) || stores.Count == MaxStoresPerZero)
                        break;
                    stores.Add(j);
                    continue;
                }
                if (ReadsA(current) || (!nzDead && ReadsNz(current)))
                    break;
                if (OverwritesA(current) && WritesNz(current))
                {
                    aDead = true;
                    break;
                }
                if (WritesNz(current))
                {
                    nzDead = true;
                    continue;
                }
                if (!PreservesAAndNz(current))
                    break;
            }
            if (!aDead || stores.Count == 0)
                continue;

            foreach (var storeIndex in stores)
            {
                var store = lines[storeIndex];
                store.Opcode = char.IsAsciiLetterLower(store.OriginalOpcode![0]) ? "stz" : "STZ";
                program.Rewritten++;
            }
            program.Remove(i, OptimizationKind.Stz);
            program.UsesCmosInstructions = true;
        }
    }

    /// <summary>
    /// For each direct JMP: removes it if its target is the very next label (looking through
    /// blank, comment and <c>.dbg</c> lines), otherwise threads it (see <see cref="ThreadJump"/>)
    /// - and checks for a jump to the next line again afterwards, since threading may have
    /// retargeted it there.
    /// </summary>
    public static void Jumps(ProgramText program)
    {
        var lines = program.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.IsDead || line.NoOptimize || !IsDirectJump(line))
                continue;
            if (!RemoveJumpToNext(program, i))
            {
                ThreadJump(program, i);
                RemoveJumpToNext(program, i);
            }
        }
    }

    /// <summary>A JMP to a plain label - not <c>JMP (indirect)</c> or an expression like <c>*+3</c>.</summary>
    private static bool IsDirectJump(AsmLine line) =>
        Is(line, "JMP") && line.Operand is { Length: > 0 } operand &&
        operand.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '@' or '.');

    private static bool RemoveJumpToNext(ProgramText program, int index)
    {
        var jump = program.Lines[index];
        if (!IsRemovable(jump) || !IsDirectJump(jump))
            return false;
        var next = program.NextSignificant(index);
        if (next < 0 || program.Lines[next].Label != jump.Operand)
            return false;
        program.Remove(index, OptimizationKind.Jump);
        return true;
    }

    private const int MaxThreadHops = 16;

    /// <summary>
    /// <c>JMP L1</c> landing on <c>JMP L2</c> (or <c>BRA L2</c>) becomes <c>JMP L2</c>, following
    /// chains; a JMP landing on RTS becomes RTS (2 bytes and 3 cycles less). Labels are looked up
    /// only within the JMP's own <c>.proc</c>. A chain that cycles (an intentional infinite loop)
    /// is left alone, and nothing is threaded into a <c>#NOOPT</c> region. Only the JMP itself is
    /// rewritten - its size never grows, so no branch elsewhere can be pushed out of range.
    /// </summary>
    private static void ThreadJump(ProgramText program, int index)
    {
        var lines = program.Lines;
        var jump = lines[index];
        var target = jump.Operand!;
        var visited = new List<string> { target };
        var hops = 0;

        for (; hops < MaxThreadHops; hops++)
        {
            var label = program.FindLabel(index, target);
            if (label < 0)
                break;
            var landingIndex = program.InstructionAt(label);
            if (landingIndex == index)
                return;
            if (landingIndex < 0 || lines[landingIndex].NoOptimize)
                break;
            var landing = lines[landingIndex];

            if (Is(landing, "RTS") && landing.Operand is null)
            {
                jump.Opcode = char.IsAsciiLetterLower(jump.Opcode![0]) ? "rts" : "RTS";
                jump.Operand = null;
                program.RecordRewrite(OptimizationKind.Thread, 2, 3);
                return;
            }

            // A BRA in the file is unconditional whatever the CPU option says: cc65 only emits it
            // for a CPU that has it.
            var onward = IsDirectJump(landing) || (Is(landing, "BRA") && landing.Operand is not null);
            if (!onward)
                break;
            if (visited.Contains(landing.Operand!))
                return;
            target = landing.Operand!;
            visited.Add(target);
        }

        if (hops == 0)
            return;
        jump.Operand = target;
        program.RecordRewrite(OptimizationKind.Thread, 0, 3 * hops);
    }
}
