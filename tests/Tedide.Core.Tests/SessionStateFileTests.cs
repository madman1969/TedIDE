namespace Tedide.Core.Tests;

public class SessionStateFileOpenFilesTests
{
    [Fact]
    public void OpenFiles_RoundTrip_AndAreNullInAFileFromBeforeTabs()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "Game.session.json");
            new SessionStateFile { LastOpenFile = "src/b.c", OpenFiles = ["src/a.c", "src/b.c"] }.Save(path);

            var loaded = SessionStateFile.Load(path);
            Assert.Equal("src/b.c", loaded.LastOpenFile);
            Assert.Equal(["src/a.c", "src/b.c"], loaded.OpenFiles);

            File.WriteAllText(path, """{ "LastOpenFile": "src/main.c" }""");
            Assert.Null(SessionStateFile.Load(path).OpenFiles);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

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

    [Fact]
    public void LoadOrRecover_ReturnsAnEmptyState_AndMovesACorruptFileAside_InsteadOfThrowing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "Test.session.json");
            File.WriteAllText(path, "not json at all");

            var loaded = SessionStateFile.LoadOrRecover(path, out var problem);

            Assert.Null(loaded.LastOpenFile);
            Assert.NotNull(problem);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".corrupt"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
