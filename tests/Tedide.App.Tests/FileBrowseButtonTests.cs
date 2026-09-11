using Tedide.App.Views;

namespace Tedide.App.Tests;

public class FileBrowseButtonTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NearestExistingPathOrDirectory_ReturnsNull_ForNullOrEmpty(string? path)
    {
        Assert.Null(FileBrowseButton.NearestExistingPathOrDirectory(path));
    }

    [Fact]
    public void NearestExistingPathOrDirectory_ReturnsThePathItself_WhenItsAFileThatExists()
    {
        var file = Path.GetTempFileName();
        try
        {
            Assert.Equal(file, FileBrowseButton.NearestExistingPathOrDirectory(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void NearestExistingPathOrDirectory_ReturnsTheContainingDirectory_WhenTheFileDoesNotExistButItsDirectoryDoes()
    {
        var missingFile = Path.Combine(Path.GetTempPath(), "does-not-exist.cfg");

        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            FileBrowseButton.NearestExistingPathOrDirectory(missingFile));
    }

    [Fact]
    public void NearestExistingPathOrDirectory_WalksUpToTheNearestExistingAncestor_WhenSeveralLevelsAreMissing()
    {
        var deeplyMissingPath = Path.Combine(Path.GetTempPath(), "no", "such", "folder", "vic20.cfg");

        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            FileBrowseButton.NearestExistingPathOrDirectory(deeplyMissingPath));
    }

    [Fact]
    public void NearestExistingPathOrDirectory_ReturnsNull_WhenNothingAlongThePathExists()
    {
        // An actually-unmounted drive letter (found dynamically, not assumed - a mapped network
        // drive could otherwise make a hardcoded guess flaky) - nothing to walk up to, so this
        // should fall all the way through to null rather than throwing.
        var mountedLetters = Directory.GetLogicalDrives().Select(d => char.ToUpperInvariant(d[0])).ToHashSet();
        var unmountedLetter = Enumerable.Range('A', 26)
            .Select(c => (char)c)
            .First(letter => !mountedLetters.Contains(letter));
        var path = $@"{unmountedLetter}:\no\such\drive\vic20.cfg";

        Assert.Null(FileBrowseButton.NearestExistingPathOrDirectory(path));
    }
}
