using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Tedide.Git;

/// <summary>What one git command did: its exit code and its output, both streams as UTF-8 text.</summary>
public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>git's own explanation of a failure - its error output, else its normal output.</summary>
    public string Message => (Error.Length > 0 ? Error : Output).Trim();
}

/// <summary>
/// Runs the git command line. git rather than a library: it's the user's own git, so their config,
/// hooks, line-ending rules and credential manager all apply, and there's no native library to ship
/// in the single-file publish. Every run is kept off Tedide's console the same way the cc65 tools
/// are (no window, input redirected - a console child otherwise resets the console's input mode and
/// mouse clicks start typing escape sequences into the editor), and can never stop to ask for a
/// password in the terminal.
/// </summary>
internal static class GitRunner
{
    /// <summary>The git executable - "git" on PATH.</summary>
    internal static string Executable { get; set; } = "git";

    internal static async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments,
        string? standardInput = null, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        // Paths come back as they are, not octal-escaped; and a status check doesn't take the index
        // lock, so it can't make the user's own git command fail while Tedide refreshes.
        foreach (var argument in (string[])["-c", "core.quotepath=false", .. arguments])
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Couldn't start '{Executable}'.");
        }
        catch (Win32Exception ex)
        {
            return new GitResult(-1, "", $"git couldn't be started - is it installed and on PATH? ({ex.Message})");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            if (standardInput is not null)
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                throw;
            }
            return new GitResult(process.ExitCode, await output, await error);
        }
    }
}
