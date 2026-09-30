using System.Globalization;

namespace Tedide.Build;

/// <summary>
/// What opt6502 did to one file (or, summed with <see cref="Add"/>, to a whole build), parsed from
/// the single <c>opt6502-stats:</c> line Tedide's fork prints under <c>-quiet</c> (see
/// tools/opt6502/TEDIDE.md) - space-separated <c>key=value</c> pairs, so a field added to the
/// fork later is simply carried through <see cref="ByKind"/> or ignored rather than breaking this.
/// <see cref="Bytes"/> and <see cref="Cycles"/> are opt6502's own estimates from each changed
/// instruction's addressing mode, hence the "~" wherever they're shown.
/// </summary>
public sealed record Opt6502Stats(
    int Optimizations,
    int Removed,
    int Rewritten,
    int Bytes,
    int Cycles,
    IReadOnlyDictionary<string, int> ByKind)
{
    public const string LinePrefix = "opt6502-stats:";

    public static readonly Opt6502Stats Empty = new(0, 0, 0, 0, 0, new Dictionary<string, int>());

    /// <summary>The per-kind counters opt6502 reports, in its own order, with the wording the
    /// Output panel uses for each.</summary>
    private static readonly (string Key, string Description)[] Kinds =
    [
        ("reload", "redundant reload"),
        ("constant", "repeated constant load"),
        ("transfer", "redundant transfer"),
        ("jump", "jump to next line"),
        ("unreachable", "unreachable instruction"),
        ("stz", "STZ rewrite"),
        ("inline", "runtime call inlined"),
        ("thread", "jump threaded"),
    ];

    private static readonly HashSet<string> TotalKeys = ["optimizations", "removed", "rewritten", "bytes", "cycles"];

    /// <summary>Parses an <c>opt6502-stats:</c> line; false for any other line.</summary>
    public static bool TryParse(string line, out Opt6502Stats stats)
    {
        stats = Empty;
        if (!line.StartsWith(LinePrefix, StringComparison.Ordinal))
            return false;

        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var field in line[LinePrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = field.IndexOf('=');
            if (equals > 0 && int.TryParse(field[(equals + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                values[field[..equals]] = value;
        }

        int Get(string key) => values.GetValueOrDefault(key);
        stats = new Opt6502Stats(
            Get("optimizations"), Get("removed"), Get("rewritten"), Get("bytes"), Get("cycles"),
            values.Where(kv => !TotalKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
        return true;
    }

    /// <summary>The field-by-field sum of this and <paramref name="other"/>.</summary>
    public Opt6502Stats Add(Opt6502Stats other)
    {
        var byKind = new Dictionary<string, int>(ByKind);
        foreach (var (key, value) in other.ByKind)
            byKind[key] = byKind.GetValueOrDefault(key) + value;
        return new Opt6502Stats(
            Optimizations + other.Optimizations, Removed + other.Removed, Rewritten + other.Rewritten,
            Bytes + other.Bytes, Cycles + other.Cycles, byKind);
    }

    /// <summary>
    /// One human-readable summary, e.g. "6 optimizations (5 jump to next line, 1 STZ rewrite),
    /// ~17 bytes and ~17 cycles saved", or "no optimizations found". -speed's runtime inlining
    /// trades size for speed, so <see cref="Bytes"/> can be negative - then it reads "~190 bytes
    /// added, ~126 cycles saved". Cycles are per run through each changed line, so for inlining
    /// they're saved again on every pass round the loop.
    /// </summary>
    public string Describe()
    {
        if (Optimizations == 0)
            return "no optimizations found";

        var known = Kinds.Select(k => k.Key).ToHashSet();
        var parts = Kinds
            .Where(k => ByKind.GetValueOrDefault(k.Key) > 0)
            .Select(k => $"{ByKind[k.Key]} {k.Description}")
            .Concat(ByKind.Where(kv => kv.Value > 0 && !known.Contains(kv.Key)).Select(kv => $"{kv.Value} {kv.Key}"));
        var noun = Optimizations == 1 ? "optimization" : "optimizations";
        var size = Bytes >= 0 ? $"~{Bytes} bytes and ~{Cycles} cycles saved" : $"~{-Bytes} bytes added, ~{Cycles} cycles saved";
        return $"{Optimizations} {noun} ({string.Join(", ", parts)}), {size}";
    }
}
