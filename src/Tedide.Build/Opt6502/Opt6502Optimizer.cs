using Tedide.Core;

namespace Tedide.Build.Opt6502;

/// <summary>What <see cref="Opt6502Optimizer"/> optimizes for and the CPU it may use.</summary>
/// <param name="Mode">Size never adds code; Speed also inlines cc65 runtime calls in loops.</param>
/// <param name="Cpu">"6502", "65c02" or "65816" (see <see cref="Cc65TargetExtensions.Opt6502Cpu"/>).
/// The CMOS ones allow STZ.</param>
/// <param name="InlineLimit">Debugging aid - see <see cref="RuntimeInliner.Run"/>.</param>
public sealed record Opt6502Options(Opt6502Mode Mode = Opt6502Mode.Size, string Cpu = "6502", int? InlineLimit = null);

/// <summary>The optimized source and what was done to it.</summary>
public sealed record Opt6502Result(string Output, Opt6502Stats Stats);

/// <summary>
/// Tedide's optimizer for the assembly cc65 generates - an MIT reimplementation of the patched
/// opt6502 fork Tedide used to run as a separate GPL program, behaving identically on cc65 output.
/// Works on ca65 source only. Every change is one the instruction rules (<see cref="Instructions"/>)
/// prove leaves the program's behaviour unchanged, and anything inside a <c>;#NOOPT</c> ...
/// <c>;#OPT</c> region is left alone.
/// </summary>
public static class Opt6502Optimizer
{
    private const int MaxPasses = 10;

    public static Opt6502Result Optimize(string source, Opt6502Options options, string? newline = null)
    {
        var cmos = options.Cpu.ToLowerInvariant() switch
        {
            "6502" => false,
            "65c02" or "65816" => true,
            _ => throw new ArgumentException($"Unknown CPU '{options.Cpu}' - expected 6502, 65c02 or 65816.", nameof(options)),
        };

        var program = ProgramText.Parse(source);

        if (options.Mode == Opt6502Mode.Speed)
            RuntimeInliner.Run(program, options.InlineLimit);

        // Each pass can expose another's pattern, so they repeat until a round finds nothing new.
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var before = program.Optimizations;
            Passes.RedundantReloads(program);
            Passes.RedundantTransfers(program);
            Passes.RepeatedConstantLoads(program);
            Passes.StoreZero(program, cmos);
            // Unreachable code is cleared before the jump pass too, so a JMP over it is seen as a
            // jump to the next line (removed) rather than threaded.
            Passes.UnreachableCode(program);
            Passes.Jumps(program);
            Passes.UnreachableCode(program);
            if (program.Optimizations == before)
                break;
        }

        return new Opt6502Result(program.Write(newline ?? Environment.NewLine), program.Stats());
    }
}
