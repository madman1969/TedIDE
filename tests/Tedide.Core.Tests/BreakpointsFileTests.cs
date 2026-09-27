namespace Tedide.Core.Tests;

public class BreakpointsFileTests
{
    [Fact]
    public void Load_ReturnsAnEmptySet_WhenTheFileDoesNotExist()
    {
        var loaded = BreakpointsFile.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.breakpoints.json"));

        Assert.Empty(loaded.Breakpoints);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsEveryBreakpointField()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "Test.breakpoints.json");
            var file = new BreakpointsFile
            {
                Breakpoints =
                [
                    new BreakpointEntry("src/main.c", 19, true),
                    new BreakpointEntry("src/screen.c", 42, false),
                ],
            };

            file.Save(path);
            var loaded = BreakpointsFile.Load(path);

            Assert.Equal(2, loaded.Breakpoints.Count);
            Assert.Equal(new BreakpointEntry("src/main.c", 19, true), loaded.Breakpoints[0]);
            Assert.Equal(new BreakpointEntry("src/screen.c", 42, false), loaded.Breakpoints[1]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadOrRecover_ReturnsAnEmptySet_AndMovesACorruptFileAside_InsteadOfThrowing()
    {
        // A hand-edited, now-invalid breakpoints file used to stop the whole project opening.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "Test.breakpoints.json");
            File.WriteAllText(path, "{ \"Breakpoints\": [ { \"SourceFile\": ");

            var loaded = BreakpointsFile.LoadOrRecover(path, out var problem);

            Assert.Empty(loaded.Breakpoints);
            Assert.NotNull(problem);
            Assert.Contains("Test.breakpoints.json.corrupt", problem);
            Assert.False(File.Exists(path));
            // The broken content survives for repair by hand, rather than being overwritten the
            // next time a breakpoint is toggled.
            Assert.Equal("{ \"Breakpoints\": [ { \"SourceFile\": ", File.ReadAllText(path + ".corrupt"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadOrRecover_ReportsNoProblem_ForAValidOrMissingFile()
    {
        var missing = BreakpointsFile.LoadOrRecover(Path.Combine(Path.GetTempPath(), "does-not-exist.breakpoints.json"), out var problem);

        Assert.Empty(missing.Breakpoints);
        Assert.Null(problem);
    }

    [Fact]
    public void Load_DropsNullsThatValidJsonCanStillContain()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var nullList = Path.Combine(dir.FullName, "a.breakpoints.json");
            File.WriteAllText(nullList, "{ \"Breakpoints\": null }");
            var nullEntries = Path.Combine(dir.FullName, "b.breakpoints.json");
            File.WriteAllText(nullEntries, "{ \"Breakpoints\": [ null, { \"Line\": 3 }, { \"SourceFile\": \"src/main.c\", \"Line\": 5 } ] }");

            Assert.Empty(BreakpointsFile.Load(nullList).Breakpoints);
            Assert.Equal([new BreakpointEntry("src/main.c", 5)], BreakpointsFile.Load(nullEntries).Breakpoints);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
