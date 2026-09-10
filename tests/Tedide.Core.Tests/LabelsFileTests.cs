namespace Tedide.Core.Tests;

public class LabelsFileTests
{
    // Real VICE-format label output, captured by building samples/HelloCBM with the Linker tab's
    // "Export labels" toggle on - see LabelsFile's own doc comment.
    private static string LoadFixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "HelloCBM.lbl"));

    [Fact]
    public void Parse_FindsEveryLabel()
    {
        var labels = LabelsFile.Parse(LoadFixture());

        Assert.NotEmpty(labels);
        Assert.All(labels, l => Assert.False(string.IsNullOrEmpty(l.Name)));
    }

    [Fact]
    public void Parse_ParsesTheAddressAsHex_AndStripsTheLeadingDotFromTheSymbolName()
    {
        var labels = LabelsFile.Parse(LoadFixture());

        // ld65 can legitimately emit the same symbol/address pair more than once (e.g. a KERNAL
        // routine exported under both its own module and a re-export) - First, not Single.
        var bsout = labels.First(l => l.Name == "BSOUT");
        Assert.Equal(0x00FFD2, bsout.Address);
    }

    [Fact]
    public void Parse_RecordsTheLabelFileLineNumber_ForEachLabel()
    {
        var labels = LabelsFile.Parse(LoadFixture());
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HelloCBM.lbl");
        var lines = File.ReadAllLines(fixturePath);

        var bsout = labels.First(l => l.Name == "BSOUT");

        Assert.Contains(".BSOUT", lines[bsout.LabelFileLineNumber - 1]);
    }
}
