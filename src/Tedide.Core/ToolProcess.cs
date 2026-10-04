using System.ComponentModel;
using System.Diagnostics;

namespace Tedide.Core;

/// <summary>What a console tool printed, and its exit code. Output and Error are empty when they
/// were reported line by line instead.</summary>
public sealed record ToolResult(int ExitCode, string Output, string Error);

/// <summary>
/// Runs the console tools Tedide uses - git, cl65, ar65 and build-event commands - all the same
/// way:
/// <list type="bullet">
/// <item>No console window, and input redirected. A console child otherwise shares Tedide's own
/// console, and cmd.exe resets its input mode as it runs - measured: 0x0298 (VT input, mouse)
/// became 0x028F (line input and echo, no mouse), after which every click in the editor arrived as
/// an escape sequence and was typed into the file.</item>
/// <item>Input is written and then closed, so a tool waiting for a keypress (a "pause" in a build
/// event) ends instead of hanging; a tool that stops reading early is not an error.</item>
/// <item>Cancelling kills the whole process tree - cl65 starts cc65, ca65 and ld65, which would
/// otherwise keep writing the files the next build is about to write.</item>
/// </list>
/// VICE and the Doc Viewer aren't tools: they're apps the user works in, started visibly
/// (ViceEmulator, ContextHelp).
/// </summary>
public static class ToolProcess
{
    /// <summary>A start info for <paramref name="fileName"/>, with each argument passed as it is.</summary>
    public static ProcessStartInfo StartInfo(string fileName, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo(fileName);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (workingDirectory is not null)
            startInfo.WorkingDirectory = workingDirectory;
        return Prepare(startInfo);
    }

    /// <summary>Applies the rules above to a start info set up elsewhere - a shell command line, say.</summary>
    public static ProcessStartInfo Prepare(ProcessStartInfo startInfo)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        return startInfo;
    }

    /// <summary>
    /// Runs a tool to completion. With <paramref name="onLine"/>, its output and error are reported
    /// line by line as they arrive, then null once each stream ends; without it they're collected
    /// whole. Throws <see cref="Win32Exception"/> when the tool can't be started (not installed, not
    /// on PATH), and <see cref="OperationCanceledException"/> after killing it on cancellation.
    /// </summary>
    public static async Task<ToolResult> RunAsync(ProcessStartInfo startInfo, string? standardInput = null,
        Action<string?>? onLine = null, CancellationToken cancellationToken = default)
    {
        Prepare(startInfo);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Couldn't start '{startInfo.FileName}'.");

        Task<string>? output = null, error = null;
        if (onLine is null)
        {
            output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            error = process.StandardError.ReadToEndAsync(cancellationToken);
        }
        else
        {
            process.OutputDataReceived += (_, e) => onLine(e.Data);
            process.ErrorDataReceived += (_, e) => onLine(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        try
        {
            if (standardInput is not null)
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The tool stopped reading - it failed before it needed the input (git blaming a file
            // that isn't in HEAD, say). Its exit code and message say why; the broken pipe doesn't.
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // It exited on its own between the cancellation and the kill.
            }
            throw;
        }

        return new ToolResult(process.ExitCode,
            output is null ? "" : await output.ConfigureAwait(false),
            error is null ? "" : await error.ConfigureAwait(false));
    }
}
