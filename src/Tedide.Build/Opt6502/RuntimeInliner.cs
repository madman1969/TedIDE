using System.Text;
using static Tedide.Build.Opt6502.Instructions;

namespace Tedide.Build.Opt6502;

/// <summary>
/// Speed mode only: replaces calls to cc65 runtime helpers (<see cref="RuntimeHelpers"/>) that sit
/// inside loops with the helpers' own code. cc65 compiles most stack traffic to such calls to keep
/// code small; each costs a JSR and an RTS (12 cycles) on top of the helper's work, saved on every
/// pass round the loop. Runs once, before the size passes, so they can tidy up around the
/// inlined code.
///
/// Because the bodies are cc65 2.19's, this only touches ca65 source that says it came from cc65
/// 2.19, imports the zero-page <c>sp</c> (later cc65 renamed it c_sp) and includes
/// <c>.macpack longbranch</c>, which <see cref="FixBranchRanges"/> needs.
/// </summary>
internal static class RuntimeInliner
{
    private static readonly HashSet<string> JumpsAndBranches = new(
        ["JMP", "BCC", "BCS", "BEQ", "BNE", "BMI", "BPL", "BVC", "BVS", "BRA",
         "JCC", "JCS", "JEQ", "JNE", "JMI", "JPL", "JVC", "JVS"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> LongBranches = new(
        ["JCC", "JCS", "JEQ", "JNE", "JMI", "JPL", "JVC", "JVS"], StringComparer.OrdinalIgnoreCase);

    /// <param name="inlineLimit">Debugging aid: inline only the first n call sites, so a failing
    /// program can be bisected down to the one site that breaks it. Null for no limit.</param>
    public static void Run(ProgramText program, int? inlineLimit)
    {
        var lines = program.Lines;
        var hasLongBranch = lines.Any(l => l.Source.Contains(".macpack\tlongbranch") || l.Source.Contains(".macpack longbranch"));
        var isCc65_219 = lines.Any(l => l.Source.Contains("compiler,\"cc65 v 2.19"));
        if (!isCc65_219 || !program.ZeroPageSymbols.Contains("sp") || !hasLongBranch)
            return;

        MarkLoops(program);

        var site = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var call = lines[i];
            if (!call.InLoop || call.IsDead || call.NoOptimize || call.IsInserted || !Is(call, "JSR") || call.Operand is null)
                continue;
            if (!RuntimeHelpers.ByName.TryGetValue(call.Operand, out var helper) ||
                !helper.ZeroPage.All(program.ZeroPageSymbols.Contains))
                continue;
            if (inlineLimit is { } limit && site >= limit)
                break;

            var (callBytes, _) = program.EstimateCost(call);
            var inserted = InlineCall(program, i, helper, ++site);
            var bodyBytes = inserted.Where(line => line.Opcode is not null).Sum(line => program.EstimateCost(line).Bytes);
            program.RecordInline(callBytes - bodyBytes, helper.CyclesSaved);
        }

        if (site > 0)
            FixBranchRanges(program);
    }

    /// <summary>Marks every line inside a loop: from a label to the last JMP/branch back to it,
    /// within the same <c>.proc</c>.</summary>
    private static void MarkLoops(ProgramText program)
    {
        var lines = program.Lines;
        for (var start = 0; start < lines.Count; start++)
        {
            if (lines[start].IsDead || lines[start].Label is not { } label || label[0] == '.')
                continue;

            var lastBackEdge = -1;
            for (var i = start + 1; i < lines.Count && !IsProcBoundary(lines[i], ".endproc"); i++)
            {
                var line = lines[i];
                if (!line.IsDead && line.Opcode is { } opcode && JumpsAndBranches.Contains(opcode) && line.Operand == label)
                    lastBackEdge = i;
            }
            for (var i = start; i <= lastBackEdge; i++)
                lines[i].InLoop = true;
        }
    }

    /// <summary>
    /// Inserts <paramref name="helper"/>'s body after the call at <paramref name="index"/>, which is
    /// then marked dead; returns the inserted lines. A label on the call (often the loop's own -
    /// <c>L0005: jsr decsp5</c>) moves onto a line of its own ahead of the body rather than staying
    /// on the dead call: the other passes look straight through dead lines, so a label left there
    /// would be invisible to them, and the unreachable-code pass would delete the start of the body
    /// after a preceding JMP.
    /// </summary>
    private static List<AsmLine> InlineCall(ProgramText program, int index, RuntimeHelper helper, int site)
    {
        var call = program.Lines[index];
        var inserted = new List<AsmLine>();

        if (call.Label is not null)
        {
            inserted.Add(AsmLine.Parse(call.Label + ":", inserted: true));
            call.Label = null;
        }

        var usesEnd = false;
        foreach (var statement in helper.Body)
        {
            var text = new StringBuilder();
            if (!statement.StartsWith('{'))
                text.Append('\t');
            for (var i = 0; i < statement.Length; i++)
            {
                if (statement[i] == '{' && i + 2 < statement.Length && statement[i + 2] == '}')
                {
                    usesEnd |= statement[i + 1] == 'E';
                    text.Append("@opt6502_").Append(site).Append('_').Append(statement[i + 1]);
                    i += 2;
                }
                else
                {
                    text.Append(statement[i]);
                }
            }
            inserted.Add(AsmLine.Parse(text.ToString(), inserted: true));
        }
        if (usesEnd)
            inserted.Add(AsmLine.Parse($"@opt6502_{site}_E:", inserted: true));

        program.Lines.InsertRange(index + 1, inserted);
        call.IsDead = true;
        return inserted;
    }

    /// <summary>The most bytes a line can assemble to, or -1 if that can't be bounded (a directive).</summary>
    private static int MaxSize(AsmLine line)
    {
        if (line.IsDead || IsTransparent(line))
            return 0;
        if (IsDirective(line))
            return -1;
        if (line.Opcode is null)
            return 0;
        if (LongBranches.Contains(line.Opcode))
            return 5;
        if (line.Operand is null || line.Operand.Equals("a", StringComparison.OrdinalIgnoreCase))
            return 1;
        return line.Operand[0] == '#' ? 2 : 3;
    }

    /// <summary>
    /// Inlining makes loops longer, which can push a short branch past its -128..+127 byte reach.
    /// Every short branch whose span contains inlined code is checked against a worst-case size
    /// (3 bytes for any memory operand, 5 for a longbranch macro), and if it could be out of range
    /// it becomes the matching cc65 longbranch macro (<c>beq</c> -> <c>jeq</c>), which ca65
    /// assembles as a short branch when the target is close and a branch around a JMP otherwise.
    /// A BRA, which has no such macro, becomes a JMP - unconditional, touching no flags, and
    /// 3 cycles like a taken BRA.
    /// </summary>
    private static void FixBranchRanges(ProgramText program)
    {
        var lines = program.Lines;
        var procStart = 0;
        for (var b = 0; b < lines.Count; b++)
        {
            var branch = lines[b];
            if (IsProcBoundary(branch, ".proc"))
                procStart = b;
            if (branch.IsDead || branch.Operand is not { } target || !ProgramText.IsShortBranch(branch))
                continue;

            bool found = false, spansInserted = false, unbounded = false;
            int bytes = 0, limit = 127;

            // Forward: the bytes strictly between the branch and its label.
            for (var i = b + 1; i < lines.Count && !IsProcBoundary(lines[i], ".endproc"); i++)
            {
                if (lines[i].Label == target)
                {
                    found = true;
                    break;
                }
                Measure(lines[i]);
            }

            if (!found)
            {
                // Backward, within this .proc: the bytes from the label through the branch itself.
                var targetIndex = -1;
                for (var i = procStart; i < b; i++)
                {
                    if (lines[i].Label == target)
                        targetIndex = i;
                }
                if (targetIndex < 0)
                    continue;  // not a label in this function - left to ca65
                spansInserted = unbounded = false;
                bytes = 0;
                limit = 128;
                for (var i = targetIndex; i <= b; i++)
                    Measure(lines[i]);
            }

            if (!spansInserted || (!unbounded && bytes <= limit))
                continue;

            var lower = char.IsAsciiLetterLower(branch.Opcode![0]);
            branch.Opcode = Is(branch, "BRA")
                ? (lower ? "jmp" : "JMP")
                : (lower ? "j" : "J") + branch.Opcode[1..3];
            program.Rewritten++;

            void Measure(AsmLine line)
            {
                var size = MaxSize(line);
                if (size < 0)
                    unbounded = true;
                else
                    bytes += size;
                if (line.IsInserted && !line.IsDead)
                    spansInserted = true;
            }
        }
    }
}
