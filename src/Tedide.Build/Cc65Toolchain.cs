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
    /// Builds the given project: compiles each source file in its own cl65 invocation (so each
    /// gets its own assembler listing - see <see cref="BuildCompileArguments"/>), then links the
    /// resulting object files into the project's output binary. Stops after the compile stage
    /// (skipping the link) if any source file failed to compile, so a failed build doesn't try to
    /// link with missing or stale object files.
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

        // Every source file is compiled even after an earlier one fails, so a single build shows
        // every file's errors at once rather than stopping at the first - only the link step
        // (which would just cascade unrelated "undefined symbol" errors from the missing object
        // file) is skipped once any compile has failed.
        var objectFiles = new List<string>();
        var compileFailed = false;
        var lastExitCode = 0;
        foreach (var sourceFile in project.SourceFiles)
        {
            var exitCode = await RunCl65Async(BuildCompileArguments(project, sourceFile), project.Directory, Capture, cancellationToken);
            if (exitCode is null)
            {
                stopwatch.Stop();
                return new BuildResult(false, -1, lines, Cc65DiagnosticParser.ParseAll(lines), stopwatch.Elapsed);
            }

            if (exitCode.Value != 0)
            {
                compileFailed = true;
                lastExitCode = exitCode.Value;
            }
            objectFiles.Add(Path.ChangeExtension(Path.Combine(project.Directory, sourceFile), ".o"));
        }

        if (!compileFailed)
        {
            var exitCode = await RunCl65Async(BuildLinkArguments(project, objectFiles), project.Directory, Capture, cancellationToken);
            if (exitCode is null)
            {
                stopwatch.Stop();
                return new BuildResult(false, -1, lines, Cc65DiagnosticParser.ParseAll(lines), stopwatch.Elapsed);
            }
            lastExitCode = exitCode.Value;
        }

        stopwatch.Stop();
        var diagnostics = Cc65DiagnosticParser.ParseAll(lines);
        var succeeded = !compileFailed && lastExitCode == 0 && diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
        return new BuildResult(succeeded, lastExitCode, lines, diagnostics, stopwatch.Elapsed);
    }

    /// <summary>
    /// Runs one cl65 invocation to completion, streaming its stdout/stderr lines to <paramref name="capture"/>
    /// as they arrive. Returns its exit code, or null if the process couldn't even be started
    /// (e.g. cl65 isn't on PATH) - distinct from a normal nonzero exit code, since the caller
    /// should give up immediately rather than trying further invocations against a missing toolchain.
    /// </summary>
    private async Task<int?> RunCl65Async(
        List<string> arguments,
        string workingDirectory,
        Action<string?> capture,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(Cl65Path)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start '{Cl65Path}'.");
        }
        catch (Win32Exception ex)
        {
            capture($"Could not launch '{Cl65Path}': {ex.Message}. Is cc65 installed and on PATH?");
            return null;
        }

        process.OutputDataReceived += (_, e) => capture(e.Data);
        process.ErrorDataReceived += (_, e) => capture(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    /// <summary>
    /// Deletes build artifacts without invoking cl65: the per-source .o object file cl65 leaves
    /// alongside each source file, the final linked output binary, each source file's assembler
    /// listing (if <see cref="TedideProject.GenerateAssemblyListing"/> is on), the ld65 linker map
    /// (if <see cref="TedideProject.GenerateLinkerMap"/> is on), the ld65 label file (if
    /// <see cref="TedideProject.ExportLabels"/> is on) and the debug info file (if
    /// <see cref="TedideProject.GenerateDebugInfo"/> is on). Skips whatever doesn't exist (e.g. a
    /// project that's never been built, or one that failed to compile some of its sources).
    /// Returns the full paths actually deleted.
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

        foreach (var listingFile in project.ResolvedListingFiles)
        {
            if (File.Exists(listingFile))
            {
                File.Delete(listingFile);
                removed.Add(listingFile);
            }
        }

        if (File.Exists(project.ResolvedMapFile))
        {
            File.Delete(project.ResolvedMapFile);
            removed.Add(project.ResolvedMapFile);
        }

        if (File.Exists(project.ResolvedLabelsFile))
        {
            File.Delete(project.ResolvedLabelsFile);
            removed.Add(project.ResolvedLabelsFile);
        }

        if (File.Exists(project.ResolvedDebugInfoFile))
        {
            File.Delete(project.ResolvedDebugInfoFile);
            removed.Add(project.ResolvedDebugInfoFile);
        }

        return removed;
    }

    /// <summary>
    /// The cl65 arguments to compile (and assemble, but not link - <c>-c</c>) a single source
    /// file, including its own <c>-l</c> listing path if <see cref="TedideProject.GenerateAssemblyListing"/>
    /// is on. Building a project compiles each of its source files with a separate call to this
    /// (see <see cref="BuildAsync"/>) rather than listing every source file on one cl65 command
    /// line, because cl65/ca65 only ever write to one <c>-l</c> target per invocation - a single
    /// invocation covering every source file would have each file's listing silently overwrite
    /// the last, leaving only the final source file's listing behind.
    /// </summary>
    internal static List<string> BuildCompileArguments(TedideProject project, string sourceFile)
    {
        var args = new List<string>
        {
            "-t", project.Target.ToCl65Id(),
            "-c",
        };
        if (project.OptimizationLevel.ToCl65Flag() is { } optimizationFlag)
            args.Add(optimizationFlag);
        if (project.GenerateAssemblyListing)
            args.AddRange(["-l", Path.ChangeExtension(Path.Combine(project.Directory, sourceFile), ".lst")]);
        if (project.AddSourceAsComment)
            args.Add("-T");
        if (project.GenerateDebugInfo)
            args.Add("-g");
        foreach (var includePath in project.IncludePaths)
            args.AddRange(["-I", includePath]);
        foreach (var define in project.PreprocessorDefines)
            args.AddRange(["-D", define]);
        // cl65 applies flags left-to-right as it encounters them, so e.g. an "-I" include path
        // only affects source files listed after it on the command line - ExtraArguments must
        // come before the source file, not after, or flags like that silently have no effect.
        // Added after the optimization flag so a manually-specified -O* in ExtraArguments (the
        // old way of setting this, before Cc65OptimizationLevel existed) still wins. IncludePaths
        // and PreprocessorDefines are placed before ExtraArguments too, so a hand-written -I/-D in
        // ExtraArguments can still layer on top if needed.
        args.AddRange(project.ExtraArguments);
        args.Add(sourceFile);
        return args;
    }

    /// <summary>
    /// The cl65 arguments to link a project's already-compiled object files into its output
    /// binary. Run once, after every source file has been compiled with <see cref="BuildCompileArguments"/>.
    /// Any .lib files found in the project's lib/ folder (<see cref="TedideProject.ResolvedLibFiles"/>)
    /// are appended after the object files, so ld65 resolves undefined symbols from them the same
    /// way it would object-file-then-library arguments on a hand-written command line.
    /// </summary>
    internal static List<string> BuildLinkArguments(TedideProject project, IEnumerable<string> objectFiles)
    {
        var args = new List<string>
        {
            "-t", project.Target.ToCl65Id(),
            "-o", project.ResolvedOutputFile,
        };
        if (!string.IsNullOrWhiteSpace(project.LinkerConfigPath))
            args.AddRange(["-C", Path.Combine(project.Directory, project.LinkerConfigPath)]);
        if (project.GenerateDebugInfo)
            // cl65 has no top-level flag for ld65's --dbgfile (confirmed against a real cl65
            // --help - only -g/--debug-info exist, and cl65 rejects "--dbgfile" outright as an
            // unknown option), so it has to go through -Wl's linker-option passthrough instead,
            // comma-joined the same way cc65's own -Wl syntax expects multiple pieces.
            args.AddRange(["-Wl", $"--dbgfile,{project.ResolvedDebugInfoFile}"]);
        if (project.GenerateLinkerMap)
            args.AddRange(["-m", project.ResolvedMapFile]);
        if (project.ExportLabels)
            args.AddRange(["-Ln", project.ResolvedLabelsFile]);
        args.AddRange(project.ExtraArguments);
        args.AddRange(objectFiles);
        args.AddRange(project.ResolvedLibFiles);
        return args;
    }
}
