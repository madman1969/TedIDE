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
        return solution;
    }

    public void Save(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("No path specified and solution has no FilePath.");
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
        FilePath = Path.GetFullPath(path);
    }

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

    public void AddProject(TedideProject project)
    {
        if (project.FilePath is null)
            throw new InvalidOperationException("Project must be saved before it can be added to a solution.");

        var relative = Path.GetRelativePath(Directory, project.FilePath);
        if (!ProjectPaths.Contains(relative))
            ProjectPaths.Add(relative);
    }
}
