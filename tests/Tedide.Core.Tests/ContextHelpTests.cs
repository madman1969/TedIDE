namespace Tedide.Core.Tests;

public class ContextHelpTests
{
    [Theory]
    [InlineData("    cputsxy(0, 0, s);", 7, "cputsxy")]   // inside the word
    [InlineData("    cputsxy(0, 0, s);", 12, "cputsxy")]  // just past its end
    [InlineData("    cputsxy(0, 0, s);", 5, "cputsxy")]   // on its first letter
    [InlineData("        .byte $00", 11, ".byte")]        // a ca65 directive keeps its dot
    [InlineData(".proc main", 2, ".proc")]
    [InlineData("    s.field = 1;", 8, "field")]          // C member access doesn't
    [InlineData("#if defined(__C64__)", 15, "__C64__")]
    [InlineData("    x = 1;", 2, null)]                    // in the indentation
    [InlineData("", 1, null)]
    public void WordAt_FindsTheIdentifierUnderTheCaret(string line, int column, string? expected)
    {
        Assert.Equal(expected, ContextHelp.WordAt(line, column));
    }

    [Fact]
    public void CandidatePaths_StartWithTheOverride_ThenBesideTedide_ThenTheRepoBuilds()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "Tedide.DocViewer"));
            File.WriteAllText(Path.Combine(project.FullName, "Tedide.DocViewer.csproj"), "<Project />");
            var appBin = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "Tedide.App", "bin", "Debug", "net10.0"));

            var candidates = ContextHelp.CandidatePaths(appBin.FullName, @"""D:\Docs\Tedide.DocViewer.exe""");

            Assert.Equal(@"D:\Docs\Tedide.DocViewer.exe", candidates[0]);
            Assert.Equal(Path.Combine(appBin.FullName, ContextHelp.DocViewerFileName), candidates[1]);
            Assert.Contains(Path.Combine(project.FullName, "bin", "Debug", "net10.0", ContextHelp.DocViewerFileName), candidates);
            Assert.Contains(Path.Combine(root.FullName, "publish-docviewer", ContextHelp.DocViewerFileName), candidates);

            Assert.Null(ContextHelp.FindDocViewer(candidates));
            var built = Path.Combine(project.FullName, "bin", "Release", "net10.0");
            Directory.CreateDirectory(built);
            File.WriteAllText(Path.Combine(built, ContextHelp.DocViewerFileName), "");
            Assert.Equal(Path.Combine(built, ContextHelp.DocViewerFileName), ContextHelp.FindDocViewer(candidates));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateStartInfo_OpensATabInsideWindowsTerminal_ElseItsOwnWindow()
    {
        const string exe = @"C:\Tools\Tedide.DocViewer.exe";

        var tab = ContextHelp.CreateStartInfo(exe, ".byte", insideWindowsTerminal: true);
        Assert.Equal("wt.exe", tab.FileName);
        Assert.Equal(["-w", "0", "new-tab", "--title", "Tedide Docs", "-d", @"C:\Tools", exe, "--topic", ".byte"], tab.ArgumentList);

        var window = ContextHelp.CreateStartInfo(exe, null, insideWindowsTerminal: false);
        Assert.Equal(exe, window.FileName);
        Assert.True(window.UseShellExecute);
        Assert.Empty(window.ArgumentList);
    }
}
