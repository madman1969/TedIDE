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
}
