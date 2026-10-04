namespace Tedide.Debug;

/// <summary>Where a step ended, and whether it reached a different source line in time.</summary>
public sealed record StepResult(ushort Pc, bool ReachedNewLine);

/// <summary>
/// Steps by source line, not raw instruction: steps repeatedly until the source location changes
/// from where it started. Step Over executes each subroutine call as a single instruction, so
/// calls on the line run to completion. Step Into follows calls into functions that have source;
/// a call into code with none - almost always cc65's runtime library, which a line calls
/// constantly for things like pushing arguments - is run to its return rather than stopped in, so
/// stepping into a line with no calls to the project's own functions behaves like stepping over it.
/// </summary>
public static class SourceStepper
{
    /// <summary>The most instructions one step executes looking for the next source line before
    /// giving up and stopping wherever it got to - an address with no line mapping, reached by a
    /// path the stepper doesn't recognise, would otherwise single-step forever.</summary>
    public const int MaxInstructions = 500;

    /// <param name="startPc">Where execution is stopped now.</param>
    /// <param name="sourceLocation">The project source line an address belongs to, or null for code
    /// with none. Only the project's own lines: cc65's runtime library has line records too, and
    /// Step Into must run through that code, not stop in it.</param>
    public static async Task<StepResult> StepAsync(IDebugTarget target, ushort startPc,
        Func<long, (string FilePath, int Line)?> sourceLocation, bool stepInto,
        int maxInstructions = MaxInstructions, CancellationToken cancellationToken = default)
    {
        var startLocation = sourceLocation(startPc);
        // cl65's generated .s is tracked far more finely than the C source: stepping from a C line
        // passes through addresses that only resolve to the generated assembly before reaching the
        // next C statement. Those are skipped, or Step would stop an instruction early and show the
        // .s file instead of the .c one - unless the step started in assembly.
        var startedInAssembly = startLocation is null || IsAssemblySourceFile(startLocation.Value.FilePath);

        var pc = startPc;
        for (var i = 0; i < maxInstructions; i++)
        {
            pc = await target.StepAsync(stepOverSubroutines: !stepInto, cancellationToken).ConfigureAwait(false);
            var location = sourceLocation(pc);

            if (stepInto && location is null && startLocation is not null)
            {
                // Stepped from source into code with none: run it back out. Execute Until Return
                // stops after the *next* RTS, which is a nested call's if this routine makes any,
                // so keep going until the PC is somewhere with source again.
                while (location is null && i++ < maxInstructions)
                {
                    pc = await target.ExecuteUntilReturnAsync(cancellationToken).ConfigureAwait(false);
                    location = sourceLocation(pc);
                }
            }

            if (!startedInAssembly && location is { } candidate && IsAssemblySourceFile(candidate.FilePath))
                continue;

            if (location is null || location != startLocation)
                return new StepResult(pc, ReachedNewLine: true);
        }
        return new StepResult(pc, ReachedNewLine: false);
    }

    public static bool IsAssemblySourceFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".s", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".asm", StringComparison.OrdinalIgnoreCase);
    }
}
