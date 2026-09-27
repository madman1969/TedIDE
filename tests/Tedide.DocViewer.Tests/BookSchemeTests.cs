using Tedide.Theming;

namespace Tedide.DocViewer.Tests;

/// <summary>
/// The Contents tree draws book titles in the theme's highlight colour - the same look as the
/// Solution Explorer's folders - while a selected book still looks selected.
/// </summary>
public class BookSchemeTests
{
    public static TheoryData<AppTheme> AllThemes => new(Enum.GetValues<AppTheme>());

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void BookTitles_StandOutFromPages_AndStayReadable(AppTheme theme)
    {
        var tree = ThemeSwitcher.SchemesFor(theme)["Base"];
        var book = DocViewerShell.BookScheme(tree);

        Assert.False(ThemeSwitcher.Indistinguishable(book.Normal.Foreground, tree.Normal.Foreground),
            $"{theme}: book {book.Normal.Foreground} vs page {tree.Normal.Foreground}");
        Assert.Equal(tree.Normal.Background, book.Normal.Background);
        Assert.True(ThemeSwitcher.ContrastRatio(book.Normal.Foreground, book.Normal.Background) >= ThemeSwitcher.TextContrast);
        Assert.Equal(tree.Focus, book.Focus);
        Assert.Equal(tree.Active, book.Active);
    }
}
