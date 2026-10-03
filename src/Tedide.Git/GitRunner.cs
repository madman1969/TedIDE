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

    /// <summary>
    /// <see cref="Message"/>, with the failures fetch, pull and push commonly hit put plainly first:
    /// a cancelled or impossible sign-in, a push the remote rejected, a missing SSH key. git's own
    /// words follow, so nothing is lost.
    /// </summary>
    public string Explanation
    {
        get
        {
            var message = Message;
            string? plain =
                message.Contains("User cancelled", StringComparison.OrdinalIgnoreCase)
                    ? "Sign-in was cancelled."
                : message.Contains("user interactivity has been disabled", StringComparison.OrdinalIgnoreCase)
                    ? "Git Credential Manager isn't allowed to show its sign-in window: GCM_INTERACTIVE (or credential.interactive) is set to never where Tedide was started."
                : message.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
                  || message.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
                    ? "git needs a login for this remote and couldn't get one. Check Git Credential Manager is installed (it comes with Git for Windows), or sign in once with git from a terminal."
                : message.Contains("Permission denied (publickey)", StringComparison.Ordinal)
                    ? "The remote refused your SSH key. If the key has a passphrase, load it into ssh-agent first - Tedide can't ask for it."
                : message.Contains("[rejected]", StringComparison.Ordinal)
                  && (message.Contains("fetch first", StringComparison.Ordinal) || message.Contains("non-fast-forward", StringComparison.Ordinal))
                    ? "The remote has commits you don't have yet. Pull first, then push again."
                : message.Contains("CONFLICT (", StringComparison.Ordinal)
                  || message.Contains("Automatic merge failed", StringComparison.Ordinal)
                  || message.Contains("could not apply", StringComparison.Ordinal)
                    ? "git stopped on conflicts - the files marked ! in the Git tab. Press Enter on each to resolve it, then Continue; or Abort to put everything back as it was."
                : message.Contains("would be overwritten by checkout", StringComparison.Ordinal)
                  || message.Contains("before you switch branches", StringComparison.Ordinal)
                    ? "Your uncommitted changes clash with that branch's version of the same files. Commit or discard them first, then switch."
                : message.Contains("is not fully merged", StringComparison.Ordinal)
                    ? "That branch has commits that aren't merged anywhere else, so deleting it would lose them. Tedide only deletes merged branches; use git branch -D in a terminal if you really mean to."
                : message.Contains("is not a valid branch name", StringComparison.Ordinal)
                    ? "That isn't a valid branch name: no spaces, no ~ ^ : ? * [ \\, and no \"..\"."
                : message.Contains("already exists", StringComparison.Ordinal) && message.Contains("branch", StringComparison.OrdinalIgnoreCase)
                    ? "A branch with that name already exists."
                : null;
            return plain is null ? message : $"{plain}\n\n{message}";
        }
    }
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
