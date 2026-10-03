using Tedide.Core;

namespace Tedide.Build.Tests;

/// <summary>
/// Pre- and post-build commands - see Cc65Toolchain.RunBuildEventsAsync. The project has no
/// sources, so the build is just its link step, run by a .cmd stand-in for cl65 that records that
/// it ran (and exits with whatever code the test asks for).
/// </summary>
public class BuildEventsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();

    public void Dispose() => _dir.Delete(recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_dir.FullName, name);

    private async Task<BuildResult> BuildAsync(int cl65ExitCode, IEnumerable<string> preBuild, IEnumerable<string> postBuild)
    {
        var fakeCl65 = Path("fake-cl65.cmd");
        File.WriteAllText(fakeCl65, $"@echo linked>> \"{Path("events.txt")}\"\r\n@exit /b {cl65ExitCode}\r\n");
        var project = new TedideProject { Name = "Game", Target = Cc65Target.Vic20, PreBuildCommands = [.. preBuild], PostBuildCommands = [.. postBuild] };
        project.Save(Path("Game.tproj"));
        return await new Cc65Toolchain(fakeCl65).BuildAsync(project);
    }

    private string[] Events() => File.Exists(Path("events.txt")) ? File.ReadAllLines(Path("events.txt")).Select(l => l.Trim()).ToArray() : [];

    [Fact]
    public async Task Commands_RunInOrderAroundTheBuild_InTheProjectFolder_WithMacrosExpanded()
    {
        var result = await BuildAsync(0, ["echo pre>> events.txt", "", "echo $(Target)>> events.txt"], ["echo post $(ProjectName)>> events.txt"]);

        Assert.True(result.Succeeded, string.Join("\n", result.RawOutputLines));
        Assert.Equal(["pre", "vic20", "linked", "post Game"], Events());
        Assert.Contains("prebuild> echo vic20>> events.txt", result.RawOutputLines);
        Assert.Contains("postbuild> echo post Game>> events.txt", result.RawOutputLines);
    }

    [Fact]
    public async Task AFailingPreBuildCommand_StopsTheBuildBeforeCl65Runs()
    {
        var result = await BuildAsync(0, ["exit /b 3", "echo never>> events.txt"], ["echo post>> events.txt"]);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
        Assert.Empty(Events());
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("failed with exit code 3", error.Message);
    }

    [Fact]
    public async Task PostBuildCommands_AreSkippedWhenTheLinkFails()
    {
        var result = await BuildAsync(1, [], ["echo post>> events.txt"]);

        Assert.False(result.Succeeded);
        Assert.Equal(["linked"], Events());
    }

    [Fact]
    public async Task AFailingPostBuildCommand_FailsTheBuild()
    {
        var result = await BuildAsync(0, [], ["exit /b 2"]);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("'exit /b 2' failed with exit code 2"));
    }

    [Fact]
    public async Task CommandOutput_IsCapturedLikeCl65s()
    {
        var result = await BuildAsync(0, ["echo hello from a command"], []);

        Assert.Contains("hello from a command", result.RawOutputLines);
    }

    [Fact]
    public async Task ACommandThatWaitsForAKey_EndsInsteadOfHangingTheBuild()
    {
        // Commands get no input of their own (and no access to Tedide's console, whose input mode
        // cmd.exe would otherwise reset) - so "pause" sees end-of-input at once.
        var build = BuildAsync(0, [], ["pause", "echo after>> events.txt"]);

        var result = await build.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Succeeded, string.Join("\n", result.RawOutputLines));
        Assert.Equal(["linked", "after"], Events());
    }

    [Fact]
    public void ShellStartInfo_KeepsTheCommandsOwnQuotes()
    {
        var startInfo = Cc65Toolchain.ShellStartInfo("c1541 -write \"bin\\my game.prg\" game");

        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Equal("/d /s /c \"c1541 -write \"bin\\my game.prg\" game\"", startInfo.Arguments);
    }
}
