namespace Tedide.DocViewer.Tests;

public class DocViewerLayoutSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"docviewer-layout-{Guid.NewGuid():N}", "docviewer-layout.json");

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(_path)))
            Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
    }

    [Fact]
    public void SavedDividerPosition_IsRestoredOnTheNextLoad()
    {
        new DocViewerLayoutSettings { ContentPaneWidthPercent = 55 }.Save(_path);

        Assert.Equal(55, DocViewerLayoutSettings.Load(_path).ContentPaneWidthPercent);
    }

    [Fact]
    public void MissingOrCorruptFile_FallsBackToTheDefault()
    {
        Assert.Equal(70, DocViewerLayoutSettings.Load(_path).ContentPaneWidthPercent);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");
        Assert.Equal(70, DocViewerLayoutSettings.Load(_path).ContentPaneWidthPercent);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(55, 55)]
    [InlineData(100, 90)]
    public void OutOfRangePositions_AreClampedSoBothPanesStayUsable(int saved, int expected)
    {
        Assert.Equal(expected, new DocViewerLayoutSettings { ContentPaneWidthPercent = saved }.ClampedContentPaneWidthPercent);
    }
}
