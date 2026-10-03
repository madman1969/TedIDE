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
        Assert.Equal("1234567890123456789012345678901234567890", status.Head);
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
        Assert.Null(initial.Head);
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

    [Fact]
    public void Diff_NumbersEachSideAndSkipsTheFileHeader()
    {
        var output = string.Join('\n',
            "diff --git a/HEAD-main.c b/main.c",
            "index 1111111..2222222 100644",
            "--- a/HEAD-main.c",
            "+++ b/main.c",
            "@@ -2,3 +2,4 @@ void main(void)",
            " {",
            "-    x = 0;",
            "+    x = 1;",
            "+    --y;",
            " }",
            "\\ No newline at end of file",
            "@@ -20 +21,0 @@",
            "-gone",
            "");

        var diff = GitDiff.Parse(output);

        Assert.False(diff.IsBinary);
        Assert.Equal(2, diff.Hunks.Count);
        Assert.Equal(2, diff.Added);
        Assert.Equal(2, diff.Removed);

        var first = diff.Hunks[0];
        Assert.Equal("void main(void)", first.Section);
        Assert.Equal("@@ -2,3 +2,4 @@ void main(void)", first.Header);
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Context, "{", 2, 2),
                new DiffLine(DiffLineKind.Removed, "    x = 0;", 3, null),
                new DiffLine(DiffLineKind.Added, "    x = 1;", null, 3),
                new DiffLine(DiffLineKind.Added, "    --y;", null, 4),
                new DiffLine(DiffLineKind.Context, "}", 4, 5),
            ],
            first.Lines);

        // A count left out means 1.
        var second = diff.Hunks[1];
        Assert.Equal((20, 1, 21, 0), (second.OldStart, second.OldCount, second.NewStart, second.NewCount));
        Assert.Equal(new DiffLine(DiffLineKind.Removed, "gone", 20, null), Assert.Single(second.Lines));
    }

    [Fact]
    public void Diff_OfIdenticalOrBinaryFilesHasNoHunks()
    {
        Assert.Empty(GitDiff.Parse("").Hunks);
        var binary = GitDiff.Parse("diff --git a/x b/x\nBinary files a/x and b/x differ\n");
        Assert.True(binary.IsBinary);
        Assert.Empty(binary.Hunks);
    }

    private static GitDiff Hunks(params (int OldStart, int OldCount, int NewStart, int NewCount)[] hunks) =>
        new(hunks.Select(h => new DiffHunk(h.OldStart, h.OldCount, h.NewStart, h.NewCount, "", [])).ToList());

    [Fact]
    public void LineChanges_MarksAddedModifiedAndRemovedLines()
    {
        var changes = Hunks(
            (2, 0, 3, 2),   // lines 3-4 added
            (5, 2, 7, 3),   // lines 7-9 replace two old lines
            (12, 1, 13, 0)  // a line removed after line 13
        ).LineChanges(lineCount: 20);

        Assert.Equal(
            new Dictionary<int, LineChangeKind>
            {
                [3] = LineChangeKind.Added,
                [4] = LineChangeKind.Added,
                [7] = LineChangeKind.Modified,
                [8] = LineChangeKind.Modified,
                [9] = LineChangeKind.Modified,
                [14] = LineChangeKind.RemovedAbove,
            },
            changes);
    }

    [Fact]
    public void LineChanges_MarksRemovalsAtEitherEndOfTheFile()
    {
        // Removed from the top: the new first line marks it.
        Assert.Equal(LineChangeKind.RemovedAbove, Hunks((1, 2, 0, 0)).LineChanges(5)[1]);
        // Removed from the end: the last line marks it, from below.
        Assert.Equal(LineChangeKind.RemovedBelow, Hunks((6, 2, 5, 0)).LineChanges(5)[5]);
        // Everything removed leaves the editor's one empty line.
        Assert.Equal(LineChangeKind.RemovedAbove, Hunks((1, 5, 0, 0)).LineChanges(1)[1]);
        Assert.Empty(GitDiff.Empty.LineChanges(10));
    }
}
