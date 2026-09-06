namespace Tedide.Core;

/// <summary>
/// The cc65 compiler's optimization presets, as passed to cl65's cc65 compile stage. Most of
/// these are the discrete command-line options cl65 --help lists individually (-O, -Oi, -Or,
/// -Os, -Ox); <see cref="Maximum"/> is the one exception, stacking the -i/-r/-s suffix letters
/// onto a single -O flag (-Oirs) rather than using one of the presets above. Either way, a
/// project has exactly one of these active at a time.
/// </summary>
public enum Cc65OptimizationLevel
{
    /// <summary>No optimization flag is passed - cc65's default, easiest to debug.</summary>
    None,

    /// <summary>-O: optimize code.</summary>
    Standard,

    /// <summary>-Oi: optimize code, and inline functions (increases code size).</summary>
    Inline,

    /// <summary>-Or: optimize code, and honor the register keyword.</summary>
    Register,

    /// <summary>-Os: optimize code, and inline some known functions.</summary>
    InlineKnownFunctions,

    /// <summary>-Ox: optimize code, with extended optimizations (the most aggressive built-in preset).</summary>
    Extended,

    /// <summary>-Oirs: optimize code with inlining, register variables and known-function inlining all combined - the most aggressive setting Tedide offers.</summary>
    Maximum,
}

public static class Cc65OptimizationLevelExtensions
{
    /// <summary>
    /// The cl65 command-line flag for this level, or null for <see cref="Cc65OptimizationLevel.None"/>
    /// (in which case nothing should be added to the command line at all).
    /// </summary>
    public static string? ToCl65Flag(this Cc65OptimizationLevel level) => level switch
    {
        Cc65OptimizationLevel.None => null,
        Cc65OptimizationLevel.Standard => "-O",
        Cc65OptimizationLevel.Inline => "-Oi",
        Cc65OptimizationLevel.Register => "-Or",
        Cc65OptimizationLevel.InlineKnownFunctions => "-Os",
        Cc65OptimizationLevel.Extended => "-Ox",
        Cc65OptimizationLevel.Maximum => "-Oirs",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    /// <summary>A short human-readable label pairing the flag with what it does, for the Optimizer Settings dialog.</summary>
    public static string DisplayName(this Cc65OptimizationLevel level) => level switch
    {
        Cc65OptimizationLevel.None => "None (no optimization)",
        Cc65OptimizationLevel.Standard => "-O (optimize code)",
        Cc65OptimizationLevel.Inline => "-Oi (optimize + inline functions)",
        Cc65OptimizationLevel.Register => "-Or (optimize + honor register keyword)",
        Cc65OptimizationLevel.InlineKnownFunctions => "-Os (optimize + inline known functions)",
        Cc65OptimizationLevel.Extended => "-Ox (extended optimizations)",
        Cc65OptimizationLevel.Maximum => "-Oirs (maximum optimization)",
        _ => level.ToString(),
    };

    /// <summary>Parses a <see cref="DisplayName"/> back into its <see cref="Cc65OptimizationLevel"/> - the inverse of <see cref="DisplayName"/>.</summary>
    public static bool TryParse(string text, out Cc65OptimizationLevel level)
    {
        foreach (var candidate in Enum.GetValues<Cc65OptimizationLevel>())
        {
            if (string.Equals(candidate.DisplayName(), text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                level = candidate;
                return true;
            }
        }

        level = default;
        return false;
    }
}
