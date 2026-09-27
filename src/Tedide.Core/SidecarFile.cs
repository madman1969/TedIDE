using System.Text.Json;

namespace Tedide.Core;

/// <summary>
/// Shared recovery for a project's sidecar files (<see cref="BreakpointsFile"/>,
/// <see cref="SessionStateFile"/>): these are convenience state, so one that can't be read -
/// hand-edited into invalid JSON, say - must never stop the project from opening.
/// </summary>
internal static class SidecarFile
{
    public const string CorruptSuffix = ".corrupt";

    /// <summary>
    /// Returns <paramref name="load"/>'s result, or - if the file can't be read or parsed -
    /// <paramref name="empty"/>'s, with <paramref name="problem"/> describing what happened. The
    /// unreadable file is moved aside to "{path}.corrupt" (replacing any older one) rather than
    /// left in place: the app writes this file again as soon as anything changes, which would
    /// otherwise silently destroy whatever was in it with no copy left to repair by hand.
    /// </summary>
    public static T LoadOrSetAside<T>(string path, Func<string, T> load, Func<T> empty, out string? problem)
    {
        problem = null;
        try
        {
            return load(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var reason = ex.Message;
            try
            {
                File.Move(path, path + CorruptSuffix, overwrite: true);
                problem = $"Could not read '{Path.GetFileName(path)}' ({reason}). It was moved to " +
                          $"'{Path.GetFileName(path)}{CorruptSuffix}' and an empty one will be used instead.";
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                problem = $"Could not read '{Path.GetFileName(path)}' ({reason}), and could not move it aside " +
                          $"({moveEx.Message}). An empty one is being used; it will be overwritten on the next change.";
            }
            return empty();
        }
    }
}
