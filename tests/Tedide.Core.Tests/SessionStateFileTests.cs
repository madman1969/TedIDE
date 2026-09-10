namespace Tedide.Core.Tests;

public class SessionStateFileTests
{
    [Fact]
    public void Load_ReturnsAnEmptyState_WhenTheFileDoesNotExist()
    {
        var loaded = SessionStateFile.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.session.json"));

        Assert.Null(loaded.LastOpenFile);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsTheLastOpenFile()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "Test.session.json");
            var file = new SessionStateFile { LastOpenFile = "src/main.c" };

            file.Save(path);
            var loaded = SessionStateFile.Load(path);

            Assert.Equal("src/main.c", loaded.LastOpenFile);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
