using Tedide.Theming;
using Terminal.Gui.Drawing;

namespace Tedide.DocViewer.Tests;

/// <summary>
/// The Doc Viewer's headings and links come from the app theme, not VS Code's Light+/Dark+ -
/// which gave every theme the same dark-red or orange links.
/// </summary>
public class ThemedMarkdownHighlighterTests
{
    public static TheoryData<AppTheme> AllThemes => new(Enum.GetValues<AppTheme>());

    private static Scheme Base(AppTheme theme) => ThemeSwitcher.SchemesFor(theme)["Base"];

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Links_UseTheThemesHotColor_Underlined(AppTheme theme)
    {
        var link = ThemedMarkdownHighlighter.AttributeFor(MarkdownStyleRole.Link, Base(theme))!.Value;

        Assert.Equal(Base(theme).HotNormal.Foreground, link.Foreground);
        Assert.NotEqual(TextStyle.None, link.Style & TextStyle.Underline);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Headings_AreBoldInTheThemesTypeColor_AndReadable(AppTheme theme)
    {
        var heading = ThemedMarkdownHighlighter.AttributeFor(MarkdownStyleRole.Heading, Base(theme))!.Value;

        Assert.Equal(Base(theme).CodeType.Foreground, heading.Foreground);
        Assert.NotEqual(TextStyle.None, heading.Style & TextStyle.Bold);
        Assert.True(ThemeSwitcher.ContrastRatio(heading.Foreground, heading.Background) >= ThemeSwitcher.MutedContrast);
    }

    [Theory]
    [InlineData(MarkdownStyleRole.Normal)]
    [InlineData(MarkdownStyleRole.Emphasis)]
    [InlineData(MarkdownStyleRole.CodeBlock)]
    [InlineData(MarkdownStyleRole.Table)]
    public void OtherRoles_FallBackToTheViewsOwnScheme(MarkdownStyleRole role)
    {
        Assert.Null(ThemedMarkdownHighlighter.AttributeFor(role, Base(AppTheme.SolarizedLight)));
    }

    [Fact]
    public void CodeBlocks_DontTakeTheTextMateThemesBackground()
    {
        Assert.Null(new ThemedMarkdownHighlighter().DefaultBackground);
    }
}
