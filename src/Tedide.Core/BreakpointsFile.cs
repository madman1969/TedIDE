using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tedide.Core;

/// <summary>One breakpoint, by source file (relative to the project directory, matching
/// <see cref="TedideProject.SourceFiles"/>'s own convention) and 1-based line number.</summary>
public sealed record BreakpointEntry(string SourceFile, int Line, bool Enabled = true);

/// <summary>
/// The on-disk model for a project's breakpoints ({Name}.breakpoints.json - see
/// <see cref="TedideProject.ResolvedBreakpointsFile"/>), kept separate from <see cref="TedideProject"/>
/// itself since breakpoints are session/debugging state, not build configuration - toggling one
/// shouldn't rewrite the whole .tproj file.
/// </summary>
public sealed class BreakpointsFile
{
    public List<BreakpointEntry> Breakpoints { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads breakpoints from <paramref name="path"/>, or returns an empty set if the file doesn't exist yet (a project with no breakpoints set has none).</summary>
    public static BreakpointsFile Load(string path)
    {
        if (!File.Exists(path))
            return new BreakpointsFile();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BreakpointsFile>(json, JsonOptions) ?? new BreakpointsFile();
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
