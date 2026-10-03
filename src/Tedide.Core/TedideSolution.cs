using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tedide.Core;

/// <summary>
/// The on-disk model for a .tsln solution file: an ordered list of project files.
/// </summary>
public sealed class TedideSolution
{
    public const string FileExtension = ".tsln";

    public string Name { get; set; } = "NewSolution";

    /// <summary>Project file paths, relative to the solution file's directory.</summary>
    public List<string> ProjectPaths { get; set; } = [];

    /// <summary>The project Run and Start Debugging launch (and Build Project builds), as a path
    /// relative to the solution file's directory. Null means the first application project.</summary>
    public string? StartupProject { get; set; }

    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public string Directory => FilePath is null
        ? System.Environment.CurrentDirectory
        : (Path.GetDirectoryName(Path.GetFullPath(FilePath)) ?? System.Environment.CurrentDirectory);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TedideSolution Load(string path)
    {
        var json = File.ReadAllText(path);
        var solution = JsonSerializer.Deserialize<TedideSolution>(json, JsonOptions)
            ?? throw new InvalidDataException($"Could not parse solution file '{path}'.");
        solution.FilePath = Path.GetFullPath(path);
        solution._savedJson = solution.Serialize();
        return solution;
    }

    public void Save(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("No path specified and solution has no FilePath.");
        var json = Serialize();
        File.WriteAllText(path, json);
        FilePath = Path.GetFullPath(path);
        _savedJson = json;
    }

    /// <summary>
    /// Saves only if something changed since it was loaded or last saved - what File > Save and
    /// every build do. Compared as this class would write it, not against the file's text: a file
    /// from an older Tedide lacks fields added since, and rewriting it to add them made every
    /// build show it as changed in git. True if it was written.
    /// </summary>
    public bool SaveIfChanged()
    {
        if (FilePath is not null && File.Exists(FilePath) && Serialize() == _savedJson)
            return false;
        Save();
        return true;
    }

    /// <summary>The JSON <see cref="Save"/> last wrote or <see cref="Load"/> read, as this class
    /// writes it - see <see cref="SaveIfChanged"/>.</summary>
    private string? _savedJson;

    private string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Loads every referenced project relative to this solution's directory.</summary>
    public List<TedideProject> LoadProjects()
    {
        var projects = new List<TedideProject>();
        foreach (var relativePath in ProjectPaths)
        {
            var fullPath = Path.Combine(Directory, relativePath);
            projects.Add(TedideProject.Load(fullPath));
        }
        return projects;
    }

    /// <summary>Drops a project from <see cref="ProjectPaths"/> (and as the startup project). Its files stay on disk.</summary>
    public void RemoveProject(TedideProject project)
    {
        if (project.FilePath is null)
            return;
        ProjectPaths.RemoveAll(p => SameFile(p, project.FilePath));
        if (StartupProject is { } startup && SameFile(startup, project.FilePath))
            StartupProject = null;
    }

    private bool SameFile(string relativePath, string fullPath) =>
        string.Equals(Path.GetFullPath(Path.Combine(Directory, relativePath)), Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase);

    public void AddProject(TedideProject project)
    {
        if (project.FilePath is null)
            throw new InvalidOperationException("Project must be saved before it can be added to a solution.");

        var relative = Path.GetRelativePath(Directory, project.FilePath);
        if (!ProjectPaths.Contains(relative))
            ProjectPaths.Add(relative);
    }
}
