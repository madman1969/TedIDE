using Tedide.App.Views;
using Tedide.Core;

namespace Tedide.App.Tests;

public class DebugPanelViewTests
{
    [Fact]
    public void CallStackRows_MarkOnlyTheInnermostFrame()
    {
        var rows = DebugPanelView.CallStackRows(
        [
            new CallFrame("step", "main.c:12", @"C:\p\main.c", 12),
            new CallFrame("main", "main.c:30", @"C:\p\main.c", 30),
            new CallFrame("_start", "$080D", null, 0),
        ]).ToList();

        Assert.Equal((DebugPanelView.CurrentFrameMarker, "step", "main.c:12"), rows[0]);
        Assert.Equal(("", "main", "main.c:30"), rows[1]);
        Assert.Equal(("", "_start", "$080D"), rows[2]);
    }

    [Fact]
    public void BreakpointRows_ShowStateAndCondition()
    {
        Assert.Equal(["● main.c:24", "○ main.c:30 if A == $05 (disabled)"], DebugPanelView.BreakpointRows(
        [
            new BreakpointEntry("main.c", 24),
            new BreakpointEntry("main.c", 30, Enabled: false, Condition: "A == $05"),
        ]));
    }

    [Fact]
    public void BreakpointRows_HintWhenThereAreNone() =>
        Assert.Single(DebugPanelView.BreakpointRows([]), row => row.Contains("F9", StringComparison.Ordinal));

    [Fact]
    public void DebugTitle_AddsTheStatusToTheAppTitle()
    {
        Assert.Equal("Tedide - CC65 IDE", AppShell.DebugTitle(null));
        Assert.Equal("Tedide - CC65 IDE - Stopped in main at main.c:24", AppShell.DebugTitle("Stopped in main at main.c:24"));
    }

    [Fact]
    public void PaneTabs_TitleAreas_LeavesASeparatorBetweenNames() =>
        Assert.Equal([(0, 8), (9, 16)], PaneTabs.TitleAreas(["Locals", "Watch"]));
}
