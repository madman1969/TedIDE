using System.Globalization;
using System.Text.RegularExpressions;

namespace Tedide.Core.Debugging;

/// <summary>A C function's parameter or function-level local, as cc65 records it with
/// <c>.dbg sym, "name", "type", auto, offset</c>: parameters at offsets &gt;= 0, locals below 0,
/// both relative to the function's frame (see <see cref="FunctionFrame"/>).</summary>
public sealed record FrameSymbol(string Name, int Offset)
{
    public bool IsParameter => Offset >= 0;
}

/// <summary>A frame symbol resolved at a stop: its size and address (null while not on the stack),
/// plus its declared type when the C source gave one.</summary>
public sealed record FrameSlot(FrameSymbol Symbol, int Size, ushort? Address, CVariableType? Type);

/// <summary>
/// One C function in cl65's generated assembly: its stack-allocated parameters/locals and the
/// frame depth before every instruction. A symbol at <see cref="FrameSymbol.Offset"/> lives at
/// <c>sp + depth + offset</c>, where sp is cc65's software stack pointer and depth is
/// <see cref="DepthBeforeLine"/> for the instruction about to execute - verified live against
/// VICE in samples/CBMInfo's video.c raster loop, where the same variable sits at a different
/// distance from sp on different lines as block locals come and go.
/// </summary>
/// <param name="Name">The C name (<c>video_get_text_size</c>).</param>
/// <param name="FirstLine">First line of the function in the .s file (its <c>.proc</c>).</param>
/// <param name="LastLine">Its <c>.endproc</c> line.</param>
/// <param name="DepthBeforeLine">Frame depth before the instruction on each .s line.</param>
/// <param name="Reliable">False if the function calls a stack helper this parser doesn't know
/// (or is variadic), so depths after that point can't be trusted.</param>
public sealed record FunctionFrame(
    string Name,
    int FirstLine,
    int LastLine,
    IReadOnlyList<FrameSymbol> Symbols,
    IReadOnlyDictionary<int, int> DepthBeforeLine,
    bool Reliable)
{
    /// <summary>The frame depth at the instruction on (or nearest before) <paramref name="line"/>.</summary>
    public int? DepthAt(int line)
    {
        for (var l = line; l >= FirstLine; l--)
        {
            if (DepthBeforeLine.TryGetValue(l, out var depth))
                return depth;
        }
        return null;
    }

    /// <summary>
    /// Where each symbol is at frame depth <paramref name="depth"/> with cc65's stack pointer at
    /// <paramref name="sp"/>, in declaration order (parameters, then locals). A symbol not yet on
    /// the stack - a local whose declaration hasn't run, or the last parameter before the
    /// function's first instruction pushes it - has no address.
    /// </summary>
    public IReadOnlyList<FrameSlot> SlotsAt(int depth, ushort sp, IReadOnlyDictionary<string, CVariableType> types) =>
        Symbols
            .OrderByDescending(s => s.IsParameter).ThenByDescending(s => s.Offset)
            .Select(s =>
            {
                types.TryGetValue(s.Name, out var type);
                var size = SizeFromLayout(s) ?? type?.Size ?? 2;
                var onStack = depth + s.Offset >= 0;
                return new FrameSlot(s, size, onStack ? (ushort)(sp + depth + s.Offset) : null, type);
            })
            .ToList();

    /// <summary>Byte size of each symbol that can be told from the gaps between offsets alone -
    /// cc65 records no types (every type is "00"). Locals run up to the next local (or 0),
    /// parameters up to the next parameter; the highest parameter's size is unknown.</summary>
    public int? SizeFromLayout(FrameSymbol symbol)
    {
        var sameKind = Symbols.Where(s => s.IsParameter == symbol.IsParameter).Select(s => s.Offset).Order().ToList();
        var above = sameKind.Where(o => o > symbol.Offset).Cast<int?>().FirstOrDefault();
        if (above is { } next)
            return next - symbol.Offset;
        return symbol.IsParameter ? null : -symbol.Offset;
    }
}

/// <summary>
/// Parses cl65's generated assembly (the obj/src/*.c.s files Tedide builds - see
/// Cc65Toolchain.BuildCompileSteps) for each function's stack frame. cc65 keeps C locals on a
/// software stack addressed through the zero-page pointer <c>sp</c>, and moves sp with runtime
/// helpers (<c>pushax</c>, <c>decsp2</c>, <c>incsp4</c>...) - each visible as a <c>jsr</c> in the
/// generated code, so the depth can be followed instruction by instruction:
/// <list type="bullet">
/// <item>A pushed value stays on the stack past the end of its statement only if it's a local
/// (an initialized declaration's final push); pushes still pending at the next <c>.dbg line</c>
/// become part of the frame.</item>
/// <item>A call to a C function pops its own arguments - so it clears the statement's pending
/// pushes. That can clear too much (nested calls, or a temporary like an assignment's target
/// pointer that's only popped after the call), so a later pop in the same statement takes from
/// what the call cleared before touching the frame. Every statement ends balanced, so the depth
/// is exact again by the next line - the only points a debugger stops at.</item>
/// <item><c>decsp</c>/<c>subysp</c> allocate locals directly; <c>incsp</c>/<c>addysp</c> release
/// them (or pending temporaries first).</item>
/// <item>A function with parameters pushes its last one (passed in A/X, cc65's fastcall
/// convention) as its first instruction; depths are measured from after that push, so that
/// parameter is at offset 0 and the frame starts at a negative depth before it.</item>
/// </list>
/// </summary>
public static partial class GeneratedAssemblyFrames
{
    public static IReadOnlyList<FunctionFrame> Parse(string text)
    {
        var lines = text.Split('\n');
        var frames = new List<FunctionFrame>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (ProcRegex().IsMatch(lines[i]))
                frames.Add(ParseFunction(lines, ref i));
        }
        return frames;
    }

    private static FunctionFrame ParseFunction(string[] lines, ref int index)
    {
        var firstLine = index + 1;
        string? name = null;
        var symbols = new List<FrameSymbol>();
        var depths = new Dictionary<int, int>();
        var labelDepths = new Dictionary<string, int>();
        var reliable = true;
        int permanent = 0, pending = 0, clearedByCall = 0;
        int? lastY = null;
        var sawInstruction = false;
        var unreachable = false;

        for (index++; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var lineNumber = index + 1;
            if (EndProcRegex().IsMatch(line))
                break;

            if (DbgFuncRegex().Match(line) is { Success: true } func)
            {
                name = func.Groups["name"].Value;
                continue;
            }
            if (DbgSymRegex().Match(line) is { Success: true } sym)
            {
                symbols.Add(new FrameSymbol(sym.Groups["name"].Value, int.Parse(sym.Groups["offs"].Value, CultureInfo.InvariantCulture)));
                continue;
            }
            if (DbgLineRegex().IsMatch(line))
            {
                // Statement boundary: anything still pushed is a local now.
                permanent += pending;
                pending = 0;
                clearedByCall = 0;
                continue;
            }

            var instruction = InstructionRegex().Match(line);
            if (!instruction.Success)
                continue;

            if (instruction.Groups["label"].Success)
            {
                var label = instruction.Groups["label"].Value;
                // Code after an unconditional jump is only reached through its label, so take the
                // depth recorded where something branched to it.
                if (unreachable && labelDepths.TryGetValue(label, out var labelDepth))
                    (permanent, pending) = (labelDepth, 0);
                else
                    labelDepths.TryAdd(label, permanent + pending);
                unreachable = false;
            }
            if (!instruction.Groups["op"].Success)
                continue;

            var op = instruction.Groups["op"].Value.ToLowerInvariant();
            var operand = instruction.Groups["operand"].Value.Trim();

            // The fastcall parameter push: the frame proper starts after it.
            if (!sawInstruction && symbols.Any(s => s.IsParameter) && op == "jsr" && PushSize(operand) is { } firstPush)
                permanent = -firstPush;
            sawInstruction = true;

            depths[lineNumber] = permanent + pending;

            switch (op)
            {
                case "ldy" when operand.StartsWith("#$", StringComparison.Ordinal):
                    lastY = int.Parse(operand[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    break;
                case "ldy" when operand.StartsWith('#'):
                    lastY = int.TryParse(operand[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;
                    break;
                case "ldy" or "tay" or "ply":
                    lastY = null;
                    break;
                case "iny":
                    lastY++;
                    break;
                case "dey":
                    lastY--;
                    break;
                case "jsr":
                    reliable &= ApplyCall(operand, lastY, ref permanent, ref pending, ref clearedByCall);
                    break;
                case "jmp":
                    // A jump to a label, or a tail exit (jmp incsp4) - nothing after it on this path.
                    labelDepths.TryAdd(operand, permanent + pending);
                    unreachable = true;
                    break;
                case "rts" or "rti":
                    unreachable = true;
                    break;
                default:
                    if (BranchOps.Contains(op))
                        labelDepths.TryAdd(operand, permanent + pending);
                    break;
            }
        }

        return new FunctionFrame(name ?? "?", firstLine, index + 1, symbols, depths, reliable);
    }

    /// <summary>Applies a <c>jsr</c>'s effect on the stack; false if it's a helper whose effect
    /// isn't known.</summary>
    private static bool ApplyCall(string target, int? y, ref int permanent, ref int pending, ref int clearedByCall)
    {
        if (PushSize(target) is { } push)
        {
            pending += push;
            return true;
        }
        if (Match(target, "decsp") is { } decsp)
        {
            permanent += decsp;
            return true;
        }
        if (target == "subysp")
        {
            permanent += y ?? 0;
            return y is not null;
        }
        if (PopSize(target, y) is { } pop)
        {
            // A pop takes this statement's own pushes first - including any a call was assumed to
            // have consumed but didn't (the target pointer pushed before "info->model = f();",
            // popped by the store after the call) - and only then the frame itself.
            var fromPending = Math.Min(pending, pop);
            pending -= fromPending;
            var fromCleared = Math.Min(clearedByCall, pop - fromPending);
            clearedByCall -= fromCleared;
            permanent -= pop - fromPending - fromCleared;
            return true;
        }
        // A C function (cc65 prefixes C names with "_") or a call through a pointer pops its own
        // arguments - see the class comment for why clearing all pending pushes is safe.
        if (target.StartsWith('_') || target is "callax" or "callptr4")
        {
            clearedByCall += pending;
            pending = 0;
            return true;
        }
        if (NeutralHelpers.Contains(target) || NeutralHelperRegex().IsMatch(target))
            return true;
        // Variadic functions (enter/leavey) and anything else unknown.
        return false;
    }

    private static int? PushSize(string target) => target switch
    {
        "pusha" or "pushc0" or "pushc1" or "pushc2" or "pushbysp" or "pushb0sp" => 1,
        "pushax" or "pusha0" or "pushaFF" or "pushw" or "pushwysp" or "pushw0sp" or "pushb" or "pushbidx" => 2,
        "pusheax" or "pushl0" or "push0ax" or "pushlysp" => 4,
        _ when PushConstantRegex().IsMatch(target) => 2, // push0..push7
        _ => null,
    };

    private static int? PopSize(string target, int? y) => target switch
    {
        "popa" => 1,
        "popax" or "popptr1" or "popsreg" or "staspidx" or "staxspidx" or "steaxspidx" or "tosint" => 2,
        "popeax" => 4,
        "addysp" => y,
        "addysp1" => y + 1,
        _ when Match(target, "incsp") is { } n => n,
        // Binary operators take their left operand off the stack: 4 bytes for long ("...eax"), else 2.
        _ when target.StartsWith("tos", StringComparison.Ordinal) && target != "toslong" =>
            target.EndsWith("eax", StringComparison.Ordinal) ? 4 : 2,
        _ => null,
    };

    private static int? Match(string target, string prefix) =>
        target.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(target.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    /// <summary>Helpers that read or write through sp without moving it.</summary>
    private static readonly HashSet<string> NeutralHelpers =
    [
        "ldaxysp", "ldax0sp", "ldeaxysp", "ldeax0sp", "ldaxidx", "ldaidx", "ldeaxidx", "staxysp", "stax0sp",
        "steaxysp", "steax0sp", "addeqysp", "addeq0sp", "subeqysp", "subeq0sp", "laddeqysp", "laddeq0sp",
        "lsubeqysp", "lsubeq0sp", "toslong", "incax1", "incax2", "incax3", "incax4", "incax5", "incax6",
        "incax7", "incax8", "incaxy", "decax1", "decax2", "decaxy", "negax", "negeax", "complax", "compleax",
        "bnega", "bnegax", "bnegeax", "boolne", "booleq", "boollt", "boolle", "boolgt", "boolge", "boolult",
        "boolule", "boolugt", "booluge", "axlong", "axulong", "aslax1", "aslax2", "aslax3", "aslax4",
        "asrax1", "asrax2", "asrax3", "asrax4", "shlax1", "shlax2", "shlax3", "shlax4", "shrax1", "shrax2",
        "shrax3", "shrax4", "aslaxy", "asraxy", "shlaxy", "shraxy", "shreax1", "shreax2", "shreax3",
        "shreax4", "asleax1", "asleax2", "asleax3", "asleax4", "asreax1", "asreax2", "asreax3", "asreax4",
        "mulax3", "mulax5", "mulax6", "mulax7", "mulax9", "mulax10", "umul8x16r24", "umul16x16r32",
        "udiv16by8a", "udiv32by16r16", "regswap", "regswap1", "regswap2",
    ];

    private static readonly HashSet<string> BranchOps = ["bcc", "bcs", "beq", "bne", "bmi", "bpl", "bvc", "bvs", "bra"];

    [GeneratedRegex(@"^\.proc\s")]
    private static partial Regex ProcRegex();

    [GeneratedRegex(@"^\.endproc\b")]
    private static partial Regex EndProcRegex();

    [GeneratedRegex(@"^\s*\.dbg\s+func,\s*""(?<name>[^""]+)""")]
    private static partial Regex DbgFuncRegex();

    [GeneratedRegex(@"^\s*\.dbg\s+sym,\s*""(?<name>[^""]+)"",\s*""[^""]*"",\s*auto,\s*(?<offs>-?\d+)")]
    private static partial Regex DbgSymRegex();

    [GeneratedRegex(@"^\s*\.dbg\s+line\b")]
    private static partial Regex DbgLineRegex();

    [GeneratedRegex(@"^(?:(?<label>[A-Za-z_@]\w*):)?\s*(?:(?<op>[a-zA-Z]{3})\b(?<operand>[^;]*))?\s*(?:;.*)?$")]
    private static partial Regex InstructionRegex();

    [GeneratedRegex(@"^push[0-7]$")]
    private static partial Regex PushConstantRegex();

    [GeneratedRegex(@"^(?:shl|shr|asl|asr|tst|neg|compl|bneg|bool)\w*$")]
    private static partial Regex NeutralHelperRegex();
}
