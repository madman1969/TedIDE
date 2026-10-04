namespace Tedide.App.Tests;

public class RecentProjectsSettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tedide-recent-").FullName;
    private string SettingsFile => Path.Combine(_directory, "recent.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Project(string name)
    {
        var path = Path.Combine(_directory, name + ".tsln");
        File.WriteAllText(path, "{}");
        return path;
    }

    [Fact]
    public void Touch_PutsTheProjectFirst_AndSaves()
    {
        var recent = RecentProjectsSettings.Load(SettingsFile);
        var a = Project("a");
        var b = Project("b");

        recent.Touch(a);
        recent.Touch(b);
        recent.Touch(a);

        Assert.Equal([a, b], recent.Paths);
        Assert.Equal([a, b], RecentProjectsSettings.Load(SettingsFile).Paths);
    }

    [Fact]
    public void Touch_MatchesPathsIgnoringCase()
    {
        var recent = RecentProjectsSettings.Load(SettingsFile);
        var a = Project("a");

        recent.Touch(a);
        recent.Touch(a.ToUpperInvariant());

        Assert.Single(recent.Paths);
    }

    [Fact]
    public void Touch_KeepsOnlyTheMostRecentTen()
    {
        var recent = RecentProjectsSettings.Load(SettingsFile);
        var projects = Enumerable.Range(1, RecentProjectsSettings.MaxEntries + 2).Select(i => Project($"p{i}")).ToList();

        foreach (var project in projects)
            recent.Touch(project);

        Assert.Equal(RecentProjectsSettings.MaxEntries, recent.Paths.Count);
        Assert.Equal(projects[^1], recent.Paths[0]);
        Assert.DoesNotContain(projects[0], recent.Paths);
    }

    [Fact]
    public void Remove_DropsAProjectThatFailedToOpen()
    {
        var recent = RecentProjectsSettings.Load(SettingsFile);
        var a = Project("a");
        var b = Project("b");
        recent.Touch(a);
        recent.Touch(b);

        recent.Remove(a);

        Assert.Equal([b], RecentProjectsSettings.Load(SettingsFile).Paths);
    }

    [Fact]
    public void PruneMissing_DropsProjectsThatNoLongerExist()
    {
        var recent = RecentProjectsSettings.Load(SettingsFile);
        var kept = Project("kept");
        var gone = Project("gone");
        recent.Touch(kept);
        recent.Touch(gone);
        File.Delete(gone);

        recent.PruneMissing();

        Assert.Equal([kept], RecentProjectsSettings.Load(SettingsFile).Paths);
    }

    [Fact]
    public void Load_GivesAnEmptyList_WhenTheFileIsMissingOrBroken()
    {
        Assert.Empty(RecentProjectsSettings.Load(SettingsFile).Paths);
        File.WriteAllText(SettingsFile, "{ not json");
        Assert.Empty(RecentProjectsSettings.Load(SettingsFile).Paths);
    }
}
