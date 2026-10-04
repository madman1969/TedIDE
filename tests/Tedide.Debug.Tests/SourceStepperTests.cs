using Tedide.Debug;

namespace Tedide.Debug.Tests;

public class SourceStepperTests
{
    // A made-up line table: main.c lines 10 and 11, the generated main.s between them, a project
    // function in util.c, and runtime library code (no source) at $9000 and up.
    private static (string FilePath, int Line)? Source(long address) => address switch
    {
        >= 0x1000 and < 0x1010 => ("src/main.c", 10),
        >= 0x1010 and < 0x1020 => ("src/main.s", 40),
        >= 0x1020 and < 0x1030 => ("src/main.c", 11),
        >= 0x2000 and < 0x2010 => ("src/util.c", 5),
        _ => null,
    };

    [Fact]
    public async Task StepOver_StopsAtTheNextCLine_SkippingTheGeneratedAssemblyBetween()
    {
        var target = new FakeDebugTarget(stepPcs: [0x1004, 0x1012, 0x1015, 0x1020]);

        var result = await SourceStepper.StepAsync(target, 0x1000, Source, stepInto: false);

        Assert.Equal(new StepResult(0x1020, ReachedNewLine: true), result);
        Assert.All(target.Calls, call => Assert.Equal("step over", call));
    }

    [Fact]
    public async Task StepInto_StopsInAProjectFunction()
    {
        var target = new FakeDebugTarget(stepPcs: [0x1004, 0x2000]);

        var result = await SourceStepper.StepAsync(target, 0x1000, Source, stepInto: true);

        Assert.Equal(new StepResult(0x2000, ReachedNewLine: true), result);
        Assert.Equal(["step into", "step into"], target.Calls);
    }

    [Fact]
    public async Task StepInto_RunsARuntimeLibraryCallBackOut_ThroughNestedReturns()
    {
        // Into $9000 (no source); the first RTS is a nested call's, landing in more library code;
        // the second gets back to main.c, still on line 10; the next step reaches line 11.
        var target = new FakeDebugTarget(stepPcs: [0x9000, 0x1022], returnPcs: [0x9100, 0x1006]);

        var result = await SourceStepper.StepAsync(target, 0x1000, Source, stepInto: true);

        Assert.Equal(new StepResult(0x1022, ReachedNewLine: true), result);
        Assert.Equal(["step into", "until return", "until return", "step into"], target.Calls);
    }

    [Fact]
    public async Task AStepThatStartsInAssembly_StopsOnTheNextAssemblyLine()
    {
        var target = new FakeDebugTarget(stepPcs: [0x1000]);

        var result = await SourceStepper.StepAsync(target, 0x1010, Source, stepInto: false);

        Assert.Equal(new StepResult(0x1000, ReachedNewLine: true), result);
    }

    [Fact]
    public async Task GivesUpAfterTheInstructionLimit_WhereItGotTo()
    {
        var target = new FakeDebugTarget(stepPcs: [0x1002, 0x1004, 0x1006]);

        var result = await SourceStepper.StepAsync(target, 0x1000, Source, stepInto: false, maxInstructions: 3);

        Assert.Equal(new StepResult(0x1006, ReachedNewLine: false), result);
    }

    [Theory]
    [InlineData("src/border.s", true)]
    [InlineData("src/BORDER.ASM", true)]
    [InlineData("src/main.c", false)]
    public void IsAssemblySourceFile_KnowsCa65Extensions(string path, bool expected) =>
        Assert.Equal(expected, SourceStepper.IsAssemblySourceFile(path));
}
