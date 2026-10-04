using System.ComponentModel;
using System.Diagnostics;
using Tedide.Core;

namespace Tedide.Core.Tests;

public class ToolProcessTests
{
    private static ProcessStartInfo Cmd(string command) => ToolProcess.StartInfo("cmd.exe", ["/d", "/c", command]);

    [Fact]
    public void StartInfo_KeepsTheToolOffTedidesConsole()
    {
        var startInfo = ToolProcess.StartInfo("cl65", ["-t", "c64", "main.c"], @"C:\work");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(["-t", "c64", "main.c"], startInfo.ArgumentList);
        Assert.Equal(@"C:\work", startInfo.WorkingDirectory);
    }

    [Fact]
    public async Task RunAsync_CollectsOutputErrorAndTheExitCode()
    {
        var result = await ToolProcess.RunAsync(Cmd("echo out& echo err 1>&2& exit 3"));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out", result.Output.Trim());
        Assert.Equal("err", result.Error.Trim());
    }

    [Fact]
    public async Task RunAsync_ReportsLinesAsTheyArrive_WhenAskedTo()
    {
        var lines = new List<string?>();

        var result = await ToolProcess.RunAsync(Cmd("echo one& echo two"), onLine: line => { lock (lines) lines.Add(line); });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal(["one", "two"], lines.Where(l => l is not null).Select(l => l!.Trim()));
    }

    [Fact]
    public async Task RunAsync_WritesTheInput()
    {
        var result = await ToolProcess.RunAsync(Cmd("sort"), "banana\r\napple\r\n");

        Assert.Equal(["apple", "banana"], result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Fact]
    public async Task RunAsync_IsFine_WhenTheToolExitsWithoutReadingItsInput()
    {
        // Enough input to fill the pipe, so writing it fails once the tool has gone.
        var input = new string('x', 1_000_000);

        var result = await ToolProcess.RunAsync(Cmd("exit 2"), input);

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_KillsTheTool_WhenCancelled()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ToolProcess.RunAsync(Cmd("ping -n 30 127.0.0.1"), cancellationToken: cancellation.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RunAsync_Throws_WhenTheToolIsntInstalled() =>
        await Assert.ThrowsAsync<Win32Exception>(() =>
            ToolProcess.RunAsync(ToolProcess.StartInfo("no-such-tool-tedide", [])));
}
