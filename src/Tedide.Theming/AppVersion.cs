using System.Reflection;

namespace Tedide.Theming;

/// <summary>
/// The running app's version, for display in a Help > About dialog. Reads it back from
/// <see cref="AssemblyInformationalVersionAttribute"/> - which the SDK auto-generates from the
/// <c>Version</c> MSBuild property (set once, for every project, in the repo's
/// <c>Directory.Build.props</c>) - rather than hardcoding the number in each app's own dialog,
/// where it would drift out of sync on the next release.
/// </summary>
public static class AppVersion
{
    /// <summary>The entry assembly's version (e.g. <c>"0.8.0"</c>), or <c>"unknown"</c> if it
    /// can't be determined - <see cref="Assembly.GetEntryAssembly"/> resolves to whichever actual
    /// app (Tedide.App.exe, Tedide.DocViewer.exe, ...) is running, so this is correct regardless of
    /// which app's AboutDialog calls it.</summary>
    public static string Current
    {
        get
        {
            var informational = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (informational is null)
                return "unknown";

            // The SDK appends "+<git commit sha>" to InformationalVersion for build traceability
            // (still there in the assembly's own metadata for anyone who wants it) - not something
            // that belongs in a user-facing "Version 0.8.0" label, so it's trimmed off here.
            var plusIndex = informational.IndexOf('+');
            return plusIndex < 0 ? informational : informational[..plusIndex];
        }
    }
}
