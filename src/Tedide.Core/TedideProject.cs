using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tedide.Core;

/// <summary>
/// The on-disk model for a .tproj project file: a cc65-buildable set of sources
/// plus the target platform and any extra cl65 arguments.
/// </summary>
public sealed class TedideProject
{
    public const string FileExtension = ".tproj";

    public string Name { get; set; } = "NewProject";

    public Cc65Target Target { get; set; } = Cc65Target.C64;

    /// <summary>The cc65 compiler optimization preset to build with. Defaults to no optimization.</summary>
    public Cc65OptimizationLevel OptimizationLevel { get; set; } = Cc65OptimizationLevel.None;

    /// <summary>Source file paths, relative to the project file's directory.</summary>
    public List<string> SourceFiles { get; set; } = [];

    /// <summary>Output binary name, relative to the project file's directory. Defaults to Name + target extension.</summary>
    public string? OutputFile { get; set; }

    /// <summary>Extra arguments appended verbatim to the cl65 command line.</summary>
    public List<string> ExtraArguments { get; set; } = [];

    /// <summary>Path this project was loaded from / will be saved to. Not serialized.</summary>
    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public string Directory => FilePath is null
        ? System.Environment.CurrentDirectory
        : (Path.GetDirectoryName(Path.GetFullPath(FilePath)) ?? System.Environment.CurrentDirectory);

    [JsonIgnore]
    public string ResolvedOutputFile =>
        Path.Combine(Directory, OutputFile ?? (Name + Target.DefaultOutputExtension()));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TedideProject Load(string path)
    {
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<TedideProject>(json, JsonOptions)
            ?? throw new InvalidDataException($"Could not parse project file '{path}'.");
        project.FilePath = Path.GetFullPath(path);
        return project;
    }

    public void Save(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("No path specified and project has no FilePath.");
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
        FilePath = Path.GetFullPath(path);
    }

    /// <summary>Absolute paths of all source files.</summary>
    [JsonIgnore]
    public IEnumerable<string> ResolvedSourceFiles => SourceFiles.Select(f => Path.Combine(Directory, f));
}
