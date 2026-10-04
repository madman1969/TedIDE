using Tedide.Core;
using Tedide.Debug;

namespace Tedide.Debug.Tests;

public class CheckpointSetTests
{
    private static readonly BreakpointEntry Main24 = new("src/main.c", 24);
    private static readonly BreakpointEntry Main30Off = new("src/main.c", 30, Enabled: false);
    private static readonly BreakpointEntry Video41 = new("src/video.c", 41, Condition: "A == $05");
    private static readonly BreakpointEntry Comment3 = new("src/main.c", 3);

    // Line 3 is a comment: no code, no address.
    private static long? Address(BreakpointEntry breakpoint) => breakpoint.Line == 3 ? null : 0x1000 + breakpoint.Line;

    [Fact]
    public async Task Arm_SetsEveryEnabledBreakpoint_WithItsCondition()
    {
        var target = new FakeDebugTarget();
        var set = new CheckpointSet();
        var reports = new List<string>();

        await set.ArmAsync(target, [Main24, Main30Off, Video41], Address, reports.Add);

        Assert.Equal(["set #1 at $1018", "set #2 at $1029", "condition #2: A == $05"], target.Calls);
        Assert.Equal(new Dictionary<BreakpointEntry, uint> { [Main24] = 1, [Video41] = 2 }, set.Numbers);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task Arm_ReportsABreakpointOnALineWithNoCode()
    {
        var reports = new List<string>();

        await new CheckpointSet().ArmAsync(new FakeDebugTarget(), [Comment3], Address, reports.Add);

        Assert.Contains("src/main.c:3", Assert.Single(reports));
    }

    [Fact]
    public async Task Arm_LeavesABreakpointOff_WhenItsConditionIsRejected()
    {
        // Stopping unconditionally where a condition was asked for would surprise mid-run.
        var target = new FakeDebugTarget();
        target.RejectedConditions.Add("A == $05");
        var set = new CheckpointSet();
        var reports = new List<string>();

        await set.ArmAsync(target, [Video41], Address, reports.Add);

        Assert.Equal(["set #1 at $1029", "condition #1: A == $05", "delete #1"], target.Calls);
        Assert.Empty(set.Numbers);
        Assert.Contains("A == $05", Assert.Single(reports));
    }

    [Fact]
    public async Task Resync_DeletesTheOldCheckpoints_AndSetsTheEnabledOnes()
    {
        var target = new FakeDebugTarget();
        var set = new CheckpointSet();
        await set.ArmAsync(target, [Main24], Address, _ => { });
        target.Calls.Clear();

        await set.ResyncAsync(target, [Main24 with { Enabled = false }, Video41 with { Condition = null }], Address, _ => { }, () => true);

        Assert.Equal(["delete #1", "set #2 at $1029"], target.Calls);
    }

    [Fact]
    public async Task Resync_DoesNothing_OnceTheSessionHasEnded()
    {
        var target = new FakeDebugTarget();

        await new CheckpointSet().ResyncAsync(target, [Main24], Address, _ => { }, stillCurrent: () => false);

        Assert.Empty(target.Calls);
    }

    [Fact]
    public async Task Clear_DeletesEveryCheckpoint()
    {
        var target = new FakeDebugTarget();
        var set = new CheckpointSet();
        await set.ArmAsync(target, [Main24, Video41 with { Condition = null }], Address, _ => { });
        target.Calls.Clear();

        await set.ClearAsync(target);

        Assert.Equal(["delete #1", "delete #2"], target.Calls);
        Assert.Empty(set.Numbers);
    }

    [Fact]
    public async Task OverlappingPasses_RunOneAtATime()
    {
        var target = new FakeDebugTarget { Yields = true };
        var set = new CheckpointSet();

        await Task.WhenAll(
            set.ResyncAsync(target, [Main24], Address, _ => { }, () => true),
            set.ResyncAsync(target, [Main24], Address, _ => { }, () => true));

        // The second pass deletes what the first set, then sets its own - never two at once.
        Assert.Equal(["set #1 at $1018", "delete #1", "set #2 at $1018"], target.Calls);
        Assert.Single(set.Numbers);
    }
}
