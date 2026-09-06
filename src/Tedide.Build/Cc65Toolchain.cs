using System.ComponentModel;
using System.Diagnostics;
using Tedide.Core;

namespace Tedide.Build;

/// <summary>
/// Drives the cc65 toolchain (cl65) as an external process to build a <see cref="TedideProject"/>.
/// Assumes cl65 (and its ca65/ld65/co65 companions) are available on PATH, unless overridden.
/// </summary>
public sealed class Cc65Toolchain(string cl65Path = "cl65")
{
    public string Cl65Path { get; } = cl65Path;

    /// <summary>Runs `cl65 --version` to confirm the toolchain is reachable.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo(Cl65Path, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
                return false;
            await process.WaitForExitAsync(cancellationToken);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the given project by invoking cl65 with its source files, target and extra arguments.
    /// </summary>
    /// <param name="project">The project to build.</param>
    /// <param name="onOutputLine">Optional callback invoked for each line of stdout/stderr as it arrives, for live output panes.</param>
    public async Task<BuildResult> BuildAsync(
        TedideProject project,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        // ld65 fails outright if the output file's directory doesn't already exist (e.g. an
        // OutputFile of "bin/Foo.prg" when "bin" hasn't been created yet) rather than creating it.
        var outputDirectory = Path.GetDirectoryName(project.ResolvedOutputFile);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var arguments = BuildArguments(project);
        var startInfo = new ProcessStartInfo(Cl65Path)
        {
            WorkingDirectory = project.Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        var lines = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        void Capture(string? line)
        {
            if (line is null)
                return;
            lock (lines)
                lines.Add(line);
            onOutputLine?.Invoke(line);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start '{Cl65Path}'.");
        }
        catch (Win32Exception ex)
        {
            var message = $"Could not launch '{Cl65Path}': {ex.Message}. Is cc65 installed and on PATH?";
            Capture(message);
            return new BuildResult(false, -1, lines, Cc65DiagnosticParser.ParseAll(lines), stopwatch.Elapsed);
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);
        stopwatch.Stop();

        var diagnostics = Cc65DiagnosticParser.ParseAll(lines);
        var succeeded = process.ExitCode == 0 && diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
        return new BuildResult(succeeded, process.ExitCode, lines, diagnostics, stopwatch.Elapsed);
    }

    /// <summary>
    /// Deletes build artifacts without invoking cl65: the per-source .o object file cl65 leaves
    /// alongside each source file, the final linked output binary, and the assembler listing file
    /// (if <see cref="TedideProject.GenerateAssemblyListing"/> is on). Skips whatever doesn't exist
    /// (e.g. a project that's never been built). Returns the full paths actually deleted.
    /// </summary>
    public IReadOnlyList<string> Clean(TedideProject project)
    {
        var removed = new List<string>();

        foreach (var sourceFile in project.ResolvedSourceFiles)
        {
            var objectFile = Path.ChangeExtension(sourceFile, ".o");
            if (File.Exists(objectFile))
            {
                File.Delete(objectFile);
                removed.Add(objectFile);
            }
        }

        if (File.Exists(project.ResolvedOutputFile))
        {
            File.Delete(project.ResolvedOutputFile);
            removed.Add(project.ResolvedOutputFile);
        }

        if (File.Exists(project.ResolvedListingFile))
        {
            File.Delete(project.ResolvedListingFile);
            removed.Add(project.ResolvedListingFile);
        }

        return removed;
    }

    internal static List<string> BuildArguments(TedideProject project)
    {
        var args = new List<string>
        {
            "-t", project.Target.ToCl65Id(),
            "-o", project.ResolvedOutputFile,
        };
        if (project.OptimizationLevel.ToCl65Flag() is { } optimizationFlag)
            args.Add(optimizationFlag);
        if (project.GenerateAssemblyListing)
            args.AddRange(["-l", project.ResolvedListingFile]);
        if (project.AddSourceAsComment)
            args.Add("-T");
        // cl65 applies flags left-to-right as it encounters them, so e.g. an "-I" include path
        // only affects source files listed after it on the command line - ExtraArguments must
        // come before SourceFiles, not after, or flags like that silently have no effect. Added
        // after the optimization flag so a manually-specified -O* in ExtraArguments (the old way
        // of setting this, before Cc65OptimizationLevel existed) still wins.
        args.AddRange(project.ExtraArguments);
        args.AddRange(project.SourceFiles);
        return args;
    }
}
