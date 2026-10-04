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

    /// <summary>Every file open in the editor's tabs when this was last saved, in tab order and
    /// relative to the project directory; <see cref="LastOpenFile"/> is the one that was showing.
    /// Null in a file written before tabs existed, which only recorded <see cref="LastOpenFile"/>.</summary>
    public List<string>? OpenFiles { get; set; }

    /// <summary>Loads session state from <paramref name="path"/>, or an empty one if the file doesn't exist yet (a project that's never been closed/switched away from has none).</summary>
    public static SessionStateFile Load(string path)
    {
        if (!File.Exists(path))
            return new SessionStateFile();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, CoreJsonContext.Default.SessionStateFile) ?? new SessionStateFile();
    }

    /// <summary>Like <see cref="Load"/>, but never throws for a corrupt or unreadable file - see
    /// <see cref="SidecarFile.LoadOrSetAside"/>. <paramref name="problem"/> is non-null when that happened.</summary>
    public static SessionStateFile LoadOrRecover(string path, out string? problem) =>
        SidecarFile.LoadOrSetAside(path, Load, () => new SessionStateFile(), out problem);

    public void Save(string path) => JsonFile.Write(path, this, CoreJsonContext.Default.SessionStateFile);
}
