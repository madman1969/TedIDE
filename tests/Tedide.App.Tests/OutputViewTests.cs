using Tedide.App.Views;

namespace Tedide.App.Tests;

public class OutputViewTests
{
    [Fact]
    public void AppendLine_KeepsRowsInStepWithTheLinesAppended()
    {
        // TextView edits the list it's loaded with - an empty one used to gain a blank first line,
        // putting every row one off from what OutputView knew about it (its colours included).
        var view = new OutputView();
        view.Clear();
        view.AppendLine("------ Build started ------");
        view.AppendLine("bounce.prg: 4091 bytes");

        Assert.Equal(2, view.Lines);
        Assert.Equal("------ Build started ------", string.Concat(view.GetLine(0).Select(c => c.Grapheme)));
    }

    [Theory]
    [InlineData("\u001b[97;40mMain CPU\u001b[0m:  RESET.", "Main CPU:  RESET.")]
    [InlineData("\u001b]0;VICE\u0007Ready", "Ready")]
    [InlineData("\u001b[1;31mError\u001b[m and \u001bc reset", "Error and  reset")]
    [InlineData("plain line", "plain line")]
    public void StripEscapeCodes_RemovesTerminalColourCodes(string line, string expected) =>
        Assert.Equal(expected, OutputView.StripEscapeCodes(line));
}
