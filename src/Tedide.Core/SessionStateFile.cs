using System.Text.Json;

namespace Tedide.Core;

/// <summary>
/// The on-disk model for a project's editor session state ({Name}.session.json - see
/// <see cref="TedideProject.ResolvedSessionFile"/>), kept separate from <see cref="TedideProject"/>
/// itself for the same reason <see cref="BreakpointsFile"/> is: which file happens to be open is
/// session state, not build configuration, so switching files shouldn't rewrite the whole .tproj.
/// </summary>
public sealed class SessionStateFile
{
    /// <summary>The editor's open file when this was last saved, relative to the project
    /// directory (matching <see cref="TedideProject.SourceFiles"/>'s own convention) - or null if
    /// nothing was open.</summary>
    public string? LastOpenFile { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Loads session state from <paramref name="path"/>, or an empty one if the file doesn't exist yet (a project that's never been closed/switched away from has none).</summary>
    public static SessionStateFile Load(string path)
    {
        if (!File.Exists(path))
            return new SessionStateFile();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SessionStateFile>(json, JsonOptions) ?? new SessionStateFile();
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
