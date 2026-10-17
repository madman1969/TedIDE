namespace Tedide.DocViewer.Tests;

public class PageFindDialogTests
{
    private const string Page = """
        # Guide

        Intro text.

        ## 1. Usage

        Some usage.

        ```c
        # not a heading - a C preprocessor line in a code block
        #define X 1
        ```

        The match is here.

        ## 2. Usage

        ## 1. Usage
        """;

    [Fact]
    public void FindHeadingAbove_ReturnsTheNearestHeading_SkippingCodeFences()
    {
        var heading = PageFindDialog.FindHeadingAbove(Page, Page.IndexOf("The match", StringComparison.Ordinal));

        Assert.NotNull(heading);
        Assert.Equal("1-usage", heading.Value.Slug);
    }

    [Fact]
    public void FindHeadingAbove_SuffixesRepeatedHeadings_TheWayTheViewDoes()
    {
        // The second "## 1. Usage" is a repeat, so the view anchors it as "1-usage-1".
        var heading = PageFindDialog.FindHeadingAbove(Page, Page.Length);

        Assert.Equal("1-usage-1", heading!.Value.Slug);
    }

    [Fact]
    public void FindHeadingAbove_ReturnsNull_WhenNoHeadingPrecedesTheMatch()
    {
        Assert.Null(PageFindDialog.FindHeadingAbove("plain text\nno headings here", 5));
    }

    [Fact]
    public void FindMatchedText_PrefersAWholeWord_OverAnEarlierPartOfALongerWord()
    {
        const string text = "Call cc65_heap first, then CC65 itself.";

        Assert.Equal(text.IndexOf("CC65 itself", StringComparison.Ordinal), PageFindDialog.FindMatchedText(text, "cc65"));
    }

    [Fact]
    public void FindMatchedText_FallsBackToPartOfAWord_ThenToMinusOne()
    {
        Assert.Equal(5, PageFindDialog.FindMatchedText("Call cc65_heap first.", "cc65"));
        Assert.Equal(-1, PageFindDialog.FindMatchedText("Nothing here.", "cc65"));
    }

    [Fact]
    public void ToSearchResult_BracketsTheMatches_AndKeepsTheFirstAsMatchedText()
    {
        // FTS5 marks matches with \u0002 and \u0003; the page's own "[link]" brackets must not count.
        var result = DocDatabase.ToSearchResult("cc65.md", "cc65 Manual", "cc65",
            "see [link] ... \u0002compiling\u0003 with \u0002cl65\u0003");

        Assert.Equal("see [link] ... [compiling] with [cl65]", result.Snippet);
        Assert.Equal("compiling", result.MatchedText);
    }

    [Fact]
    public void ToSearchResult_HasNoMatchedText_WhenTheSnippetMarksNothing()
    {
        Assert.Null(DocDatabase.ToSearchResult("a.md", "Book", "A", "no marks").MatchedText);
    }
}
