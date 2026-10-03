using Tedide.Build.Opt6502;
using Tedide.Core;

namespace Tedide.Build.Tests;

/// <summary>
/// Opt6502Golden/*.input.s through the optimizer in speed mode must give *.expected.s exactly
/// (line endings aside). The file name's middle part is the CPU. These pairs came from the C
/// opt6502 Tedide used to run, which the C# optimizer was checked against byte for byte.
/// </summary>
public class Opt6502GoldenTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var input in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Opt6502Golden"), "*.input.s").Order())
            data.Add(Path.GetFileName(input)[..^".input.s".Length]);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Optimize_GivesTheExpectedOutput(string name)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Opt6502Golden");
        var input = File.ReadAllText(Path.Combine(folder, name + ".input.s"));
        var expected = File.ReadAllText(Path.Combine(folder, name + ".expected.s")).Replace("\r\n", "\n");
        var cpu = name.Split('.')[1];

        var result = Opt6502Optimizer.Optimize(input, new Opt6502Options(Opt6502Mode.Speed, cpu), newline: "\n");

        Assert.Equal(expected, result.Output);
    }
}
