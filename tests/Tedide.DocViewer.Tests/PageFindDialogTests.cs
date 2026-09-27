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
}
