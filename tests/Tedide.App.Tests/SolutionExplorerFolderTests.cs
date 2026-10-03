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
