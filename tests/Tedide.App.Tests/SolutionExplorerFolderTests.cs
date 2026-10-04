using Tedide.App.Views;
using Tedide.Core;
using Tedide.Theming;
using Terminal.Gui.Views;

namespace Tedide.App.Tests;

/// <summary>
/// The Solution Explorer draws folder nodes (src, include, Generated Files...) in the theme's
/// type color so they stand out from the files in them.
/// </summary>
public sealed class SolutionExplorerFolderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TedideFolderTests", Guid.NewGuid().ToString("N"));

    public static TheoryData<AppTheme> AllThemes => new(Enum.GetValues<AppTheme>());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void FolderScheme_StandsOutFromFileText_AndStaysReadable(AppTheme theme)
    {
        var tree = ThemeSwitcher.SchemesFor(theme)["Base"];
        var folder = SolutionExplorerTree.FolderScheme(tree);

        Assert.False(ThemeSwitcher.Indistinguishable(folder.Normal.Foreground, tree.Normal.Foreground),
            $"{theme}: folder {folder.Normal.Foreground} vs file {tree.Normal.Foreground}");
        Assert.Equal(tree.Normal.Background, folder.Normal.Background);
        Assert.True(ThemeSwitcher.ContrastRatio(folder.Normal.Foreground, folder.Normal.Background) >= ThemeSwitcher.TextContrast);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void StartupScheme_StandsOutFromFilesAndFolders_AndStaysReadable(AppTheme theme)
    {
        var tree = ThemeSwitcher.SchemesFor(theme)["Base"];
        var startup = SolutionExplorerTree.StartupScheme(tree).Normal;

        // A colour of its own, or - in a single-hue theme - an underline.
        var ownColour = !ThemeSwitcher.Indistinguishable(startup.Foreground, tree.Normal.Foreground)
            && !ThemeSwitcher.Indistinguishable(startup.Foreground, SolutionExplorerTree.FolderScheme(tree).Normal.Foreground);
        Assert.True(ownColour != startup.Style.HasFlag(Terminal.Gui.Drawing.TextStyle.Underline), $"{theme}: {startup}");
        Assert.True(ThemeSwitcher.ContrastRatio(startup.Foreground, startup.Background) >= ThemeSwitcher.TextContrast);
        Assert.True(startup.Style.HasFlag(Terminal.Gui.Drawing.TextStyle.Bold));
        if (theme != AppTheme.AmberPhosphor)
            Assert.True(ownColour, $"{theme} has colours enough");
    }

    [Fact]
    public void OnlyTheStartupProject_IsMarked()
    {
        var workspace = new Workspace();
        var game = workspace.NewProject(Path.Combine(_directory, "Game"), "Game", Cc65Target.C64);
        var tool = workspace.AddNewProject(Path.Combine(_directory, "Tool"), "Tool", Cc65Target.C64, ProjectOutputType.Application);
        var explorer = new SolutionExplorerTree();

        explorer.Rebuild(workspace);
        Assert.Equal(["\u25B6 Game (c64)", "Tool (c64)"], Assert.Single(explorer.Objects!).Children.Select(p => p.Text));

        workspace.SetStartupProject(tool);
        explorer.Rebuild(workspace);
        Assert.Equal(["Game (c64)", "\u25B6 Tool (c64)"], Assert.Single(explorer.Objects!).Children.Select(p => p.Text));
    }

    [Fact]
    public void FolderScheme_LeavesTheSelectedRowLookAlone()
    {
        var tree = ThemeSwitcher.SchemesFor(AppTheme.SolarizedLight)["Base"];
        var folder = SolutionExplorerTree.FolderScheme(tree);

        Assert.Equal(tree.Focus, folder.Focus);
        Assert.Equal(tree.Active, folder.Active);
    }

    [Fact]
    public void Rebuild_MakesDirectoriesFolderNodes_AndFilesPlainNodes()
    {
        var workspace = new Workspace();
        workspace.NewProject(_directory, "Demo", Cc65Target.C64);
        var explorer = new SolutionExplorerTree();

        explorer.Rebuild(workspace);

        // The solution is the root, holding the project.
        var solution = Assert.Single(explorer.Objects!);
        Assert.Equal("Solution 'Demo' (1 project)", solution.Text);
        var project = Assert.Single(solution.Children);
        var children = project.Children.ToList();
        Assert.Contains(children, n => n is SolutionExplorerTree.FolderNode && n.Text == "src");
        Assert.Contains(children, n => n is SolutionExplorerTree.FolderNode && n.Text == "include");
        var main = children.Single(n => n.Text == "src").Children.Single(n => n.Text == "main.c");
        Assert.IsNotType<SolutionExplorerTree.FolderNode>(main);
        Assert.IsNotType<SolutionExplorerTree.FolderNode>(project);
    }
}
