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
}
