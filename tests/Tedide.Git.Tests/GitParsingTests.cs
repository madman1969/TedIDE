namespace Tedide.Git.Tests;

public class GitParsingTests
{
    private const string Root = @"C:\repo";

    [Fact]
    public void Status_ReadsTheBranchAndEveryKindOfChange()
    {
        var output = string.Join('\0',
            "# branch.oid 1234567890123456789012345678901234567890",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -1",
            "1 .M N... 100644 100644 100644 aaaa bbbb src/main.c",
            "1 A. N... 000000 100644 100644 0000 cccc src/new file.c",
            "2 R. N... 100644 100644 100644 dddd eeee R100 src/renamed.c",
            "src/old.c",
            "u UU N... 100644 100644 100644 100644 f1 f2 f3 include/conflict.h",
            "? notes.txt",
            "! bin/Game.prg",
            "");

        var status = GitStatus.Parse(output, Root);

        Assert.Equal("main", status.Branch);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal("main ↑2 ↓1", status.Describe());
        Assert.True(status.HasCommits);
        Assert.Equal(5, status.Files.Count);

        var modified = status.Files[0];
        Assert.Equal(@"C:\repo\src\main.c", modified.Path);
        Assert.Equal('M', modified.Marker);
        Assert.False(modified.IsStaged);
        Assert.True(modified.HasUnstagedChanges);

        var added = status.Files[1];
        Assert.Equal(@"C:\repo\src\new file.c", added.Path); // a space in a path survives -z
        Assert.Equal('A', added.Marker);
        Assert.True(added.IsStaged);
        Assert.False(added.HasUnstagedChanges);

        Assert.Equal(@"C:\repo\src\old.c", status.Files[2].OriginalPath);
        Assert.Equal('R', status.Files[2].Marker);
        Assert.Equal('!', status.Files[3].Marker);
        Assert.False(status.Files[3].IsStaged);
        Assert.Equal('?', status.Files[4].Marker);
    }

    [Fact]
    public void Status_BeforeTheFirstCommit_AndDetached()
    {
        var initial = GitStatus.Parse("# branch.oid (initial)\0# branch.head main\0", Root);
        Assert.False(initial.HasCommits);
        Assert.Equal("main", initial.Describe());

        Assert.Equal("(detached)", GitStatus.Parse("# branch.oid abc\0# branch.head (detached)\0", Root).Describe());
    }

    [Fact]
    public void Blame_ReadsAuthorTimeAndSummary()
    {
        var output = string.Join('\n',
            "1e48df5aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 12 12 1",
            "author aross",
            "author-mail <a@example.com>",
            "author-time 1759500000",
            "author-tz +0100",
            "summary Add multi-project solutions",
            "filename src/main.c",
            "\tint main(void)",
            "");

        var blame = GitBlameLine.Parse(output)!;

        Assert.True(blame.IsCommitted);
        Assert.Equal("aross", blame.Author);
        Assert.Equal("Add multi-project solutions", blame.Summary);
        Assert.Equal(TimeSpan.FromHours(1), blame.When.Offset);
        Assert.Equal("aross, 3 days ago: Add multi-project solutions", blame.Describe(blame.When.AddDays(3)));
    }

    [Fact]
    public void Blame_OfAnEditedLine_IsNotCommittedYet()
    {
        var blame = GitBlameLine.Parse("0000000000000000000000000000000000000000 3 3 1\nauthor Not Committed Yet\n\tx\n")!;
        Assert.False(blame.IsCommitted);
        Assert.Equal("Not committed yet", blame.Describe(DateTimeOffset.Now));
        Assert.Null(GitBlameLine.Parse(""));
    }

    [Theory]
    [InlineData(0.5, "just now")]
    [InlineData(1, "1 minute ago")]
    [InlineData(90, "1 hour ago")]
    [InlineData(60 * 24 * 2, "2 days ago")]
    [InlineData(60 * 24 * 65, "2 months ago")]
    [InlineData(60 * 24 * 800, "2 years ago")]
    public void Ago_RoundsDownToTheLargestUnit(double minutes, string expected)
    {
        Assert.Equal(expected, GitBlameLine.Ago(TimeSpan.FromMinutes(minutes)));
    }
}
