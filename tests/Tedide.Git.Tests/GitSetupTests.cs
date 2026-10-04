namespace Tedide.Git.Tests;

/// <summary>Making a folder a repository and pointing it at a remote, against a real git.</summary>
public sealed class GitSetupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tedide-gitsetup-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private string IgnoreFile => Path.Combine(_dir, ".gitignore");

    [Fact]
    public async Task CreateAsync_MakesARepositoryOnMain_WithTheIgnoreFile()
    {
        var (result, repository) = await GitRepository.CreateAsync(_dir, ["bin/", "obj/"]);

        Assert.True(result.Succeeded);
        Assert.Equal(Path.GetFullPath(_dir), repository!.Root, ignoreCase: true);
        Assert.Equal("bin/\nobj/\n", File.ReadAllText(IgnoreFile));
        Assert.Equal("main", (await repository.GetStatusAsync())!.Branch);
    }

    [Fact]
    public async Task CreateAsync_AddsOnlyWhatAnExistingIgnoreFileLacks()
    {
        File.WriteAllText(IgnoreFile, "obj/\n*.tmp");

        await GitRepository.CreateAsync(_dir, ["bin/", "obj/"]);

        Assert.Equal("obj/\n*.tmp\nbin/\n", File.ReadAllText(IgnoreFile));
    }

    [Fact]
    public async Task Remotes_CanBeAddedReadAndRepointed()
    {
        var (_, repository) = await GitRepository.CreateAsync(_dir, []);

        Assert.Null(await repository!.GetRemoteUrlAsync("origin"));
        Assert.True((await repository.AddRemoteAsync("origin", "https://example.com/one.git")).Succeeded);
        Assert.Equal(["origin"], await repository.GetRemotesAsync());
        Assert.Equal("https://example.com/one.git", await repository.GetRemoteUrlAsync("origin"));

        Assert.False((await repository.AddRemoteAsync("origin", "https://example.com/two.git")).Succeeded);
        Assert.True((await repository.SetRemoteUrlAsync("origin", "https://example.com/two.git")).Succeeded);
        Assert.Equal("https://example.com/two.git", await repository.GetRemoteUrlAsync("origin"));
    }
}
