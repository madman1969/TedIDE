namespace Tedide.Core;

/// <summary>
/// The macros a <see cref="TedideProject.PreBuildCommands"/>/<see cref="TedideProject.PostBuildCommands"/>
/// line can use, named after Visual Studio's own build-event macros so they read familiarly.
/// Matched case-insensitively; anything else in $(...) is left exactly as written.
/// </summary>
public static class BuildEvents
{
    /// <summary>Each macro and what it stands for, for the settings dialog's help text.</summary>
    public static readonly IReadOnlyList<(string Macro, string Meaning)> Macros =
    [
        ("$(ProjectDir)", "the project's folder"),
        ("$(ProjectName)", "the project's name"),
        ("$(OutputFile)", "the linked program"),
        ("$(OutputDir)", "the folder it's in"),
        ("$(OutputName)", "its name, no extension"),
        ("$(Target)", "the cc65 target (c64)"),
    ];

    public static string Expand(TedideProject project, string command)
    {
        var output = project.ResolvedOutputFile;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProjectDir"] = project.Directory,
            ["ProjectName"] = project.Name,
            ["OutputFile"] = output,
            ["OutputDir"] = Path.GetDirectoryName(output) ?? project.Directory,
            ["OutputName"] = Path.GetFileNameWithoutExtension(output),
            ["Target"] = project.Target.ToCl65Id(),
        };

        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < command.Length)
        {
            var start = command.IndexOf("$(", i, StringComparison.Ordinal);
            var end = start < 0 ? -1 : command.IndexOf(')', start + 2);
            if (end < 0)
            {
                result.Append(command, i, command.Length - i);
                break;
            }

            result.Append(command, i, start - i);
            var name = command[(start + 2)..end];
            result.Append(values.TryGetValue(name, out var value) ? value : command[start..(end + 1)]);
            i = end + 1;
        }
        return result.ToString();
    }
}
