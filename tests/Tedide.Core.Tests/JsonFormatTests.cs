using System.Text.Json;
using System.Text.Json.Serialization;
using Tedide.Core;

namespace Tedide.Core.Tests;

/// <summary>
/// The source-generated JSON writes exactly what the reflection-based serializer it replaced did,
/// with the options each file had - so no project, solution or sidecar file changes on its next
/// save.
/// </summary>
public class JsonFormatTests : IDisposable
{
    private static readonly JsonSerializerOptions OldOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions OldSessionOptions = new() { WriteIndented = true };

    private readonly string _directory = Directory.CreateTempSubdirectory("tedide-json-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string SamplesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "samples")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "samples");
    }

    public static TheoryData<string> SampleFiles()
    {
        var files = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(SamplesDirectory(), "*.t*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".tproj") || f.EndsWith(".tsln")))
            files.Add(Path.GetRelativePath(SamplesDirectory(), file));
        return files;
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void ASampleProjectOrSolution_IsWrittenAsBefore(string sample)
    {
        var source = Path.Combine(SamplesDirectory(), sample);
        var target = Path.Combine(_directory, Path.GetFileName(sample));

        if (sample.EndsWith(".tproj"))
        {
            var project = TedideProject.Load(source);
            var expected = JsonSerializer.Serialize(project, OldOptions);
            project.Save(target);
            Assert.Equal(expected, File.ReadAllText(target));
        }
        else
        {
            var solution = TedideSolution.Load(source);
            var expected = JsonSerializer.Serialize(solution, OldOptions);
            solution.Save(target);
            Assert.Equal(expected, File.ReadAllText(target));
        }
    }

    [Fact]
    public void BreakpointsAreWrittenAsBefore_AndReadBack()
    {
        var breakpoints = new BreakpointsFile
        {
            Breakpoints =
            [
                new BreakpointEntry("src/main.c", 17),
                new BreakpointEntry("src/video.c", 41, Enabled: false, Condition: "A == $05"),
            ],
        };
        var path = Path.Combine(_directory, "x.breakpoints.json");

        breakpoints.Save(path);

        Assert.Equal(JsonSerializer.Serialize(breakpoints, OldOptions), File.ReadAllText(path));
        Assert.Equal(breakpoints.Breakpoints, BreakpointsFile.Load(path).Breakpoints);
    }

    [Fact]
    public void SessionStateIsWrittenAsBefore_AndReadBack()
    {
        var session = new SessionStateFile { LastOpenFile = "src/main.c", OpenFiles = ["src/main.c", "include/main.h"] };
        var path = Path.Combine(_directory, "x.session.json");

        session.Save(path);

        Assert.Equal(JsonSerializer.Serialize(session, OldSessionOptions), File.ReadAllText(path));
        var read = SessionStateFile.Load(path);
        Assert.Equal(session.LastOpenFile, read.LastOpenFile);
        Assert.Equal(session.OpenFiles, read.OpenFiles);
    }

    [Fact]
    public void ReadOrDefault_GivesDefaults_ForAMissingOrBrokenFile()
    {
        var broken = Path.Combine(_directory, "broken.json");
        File.WriteAllText(broken, "{ not json");

        Assert.Empty(JsonFile.ReadOrDefault(Path.Combine(_directory, "missing.json"), TestJsonContext.Default.SessionStateFile).OpenFiles ?? []);
        Assert.Null(JsonFile.ReadOrDefault(broken, TestJsonContext.Default.SessionStateFile).LastOpenFile);
    }

    [Fact]
    public void Write_CreatesTheFolder()
    {
        var path = Path.Combine(_directory, "new", "folder", "x.session.json");

        new SessionStateFile { LastOpenFile = "a.c" }.Save(path);

        Assert.True(File.Exists(path));
    }
}

[JsonSerializable(typeof(SessionStateFile))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
