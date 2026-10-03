namespace Tedide.DocViewer.Tests;

public class TopicLookupTests
{
    [Theory]
    [InlineData("3.96 cputsxy", "cputsxy", 2)]
    [InlineData("3.96 cputsxy", "CPUTSXY", 2)]
    [InlineData("11.10 .BYT, .BYTE", ".byte", 1)]
    [InlineData("3.2.2. The while and do statements", "while", 1)]
    [InlineData("10.10 .HIBYTE", ".byte", 0)]
    [InlineData("3.97 cputsxy_extra", "cputsxy", 0)]
    public void Score_PrefersTheExactName_ThenAWholeWord(string heading, string word, int expected)
    {
        Assert.Equal(expected, TopicLookup.Score(heading, word));
    }

    [Fact]
    public void Headings_SkipFencedCode_AndNumberRepeatedSlugs()
    {
        var markdown = "# Intro\n```c\n#define X 1\n```\n## 1.1 Note\ntext\n## 1.1 Note\n";
        Assert.Equal(
            [("Intro", "intro"), ("1.1 Note", "11-note"), ("1.1 Note", "11-note-1")],
            TopicLookup.Headings(markdown).ToList());
    }

    [Fact]
    public void Find_AnExactHeadingBeatsAnEarlierPartialOne()
    {
        TopicLookup.Page[] pages =
        [
            new("ca65", "## 11.10 .BYT, .BYTE\n"),
            new("funcref", "## 3.1 memset\n"),
            new("other", "## 2 .BYTE\n"),
        ];
        Assert.Equal(("other", "2-byte"), TopicLookup.Find(pages, ".byte"));
        Assert.Equal(("funcref", "31-memset"), TopicLookup.Find(pages, "memset"));
        Assert.Null(TopicLookup.Find(pages, "nothing"));
    }

    [Theory]
    [InlineData("cputsxy", "funcref", "396-cputsxy")]
    [InlineData(".byte", "ca65", "1110-byt-byte")]
    [InlineData(".proc", "ca65", "1199-proc")]
    public void Find_InTheRealDocs(string word, string fileName, string anchor)
    {
        using var database = new DocDatabase(DocsDbPath());
        Assert.Equal((fileName, anchor), TopicLookup.Find(database.AllPages(), word));
    }

    private static string DocsDbPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Docs.db");
        return File.Exists(beside)
            ? beside
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Tedide.DocViewer", "Docs.db"));
    }
}
