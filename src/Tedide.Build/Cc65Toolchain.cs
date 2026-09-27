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
    /// Builds the given project: compiles each source file into obj/ with its own cl65
    /// invocation(s) (so each gets its own assembler listing - see <see cref="BuildCompileSteps"/>),
    /// then links the resulting object files into the project's output binary. Stops after the compile stage
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
            // cl65 won't create the obj/ subdirectory an -o/-l path points into.
            Directory.CreateDirectory(Path.GetDirectoryName(project.ResolvedObjectFileFor(sourceFile))!);

            foreach (var step in BuildCompileSteps(project, sourceFile))
            {
                var exitCode = await RunCl65Async(step, project.Directory, Capture, cancellationToken);
                if (exitCode is null)
                {
                    stopwatch.Stop();
                    return new BuildResult(false, -1, lines, Cc65DiagnosticParser.ParseAll(lines), stopwatch.Elapsed);
                }

                if (exitCode.Value != 0)
                {
                    // A C file that failed to compile has no generated assembly to assemble - skip
                    // its remaining step rather than adding a second, cascading error to the output.
                    compileFailed = true;
                    lastExitCode = exitCode.Value;
                    break;
                }
            }
            objectFiles.Add(project.ResolvedObjectFileFor(sourceFile));
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

        Process? started;
        try
        {
            started = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start '{Cl65Path}'.");
        }
        catch (Win32Exception ex)
        {
            capture($"Could not launch '{Cl65Path}': {ex.Message}. Is cc65 installed and on PATH?");
            return null;
        }

        using var process = started;
        process.OutputDataReceived += (_, e) => capture(e.Data);
        process.ErrorDataReceived += (_, e) => capture(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // WaitForExitAsync only stops *waiting* on cancellation - cl65 itself (and the
            // cc65/ca65/ld65 children it spawns) would otherwise keep running in the background,
            // still writing the very .o/.lst/output files a follow-up build is about to produce.
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Already exited on its own between the cancellation and the Kill - nothing to do.
            }
            throw;
        }
        return process.ExitCode;
    }

    /// <summary>
    /// Deletes build artifacts without invoking cl65: the whole obj/ directory
    /// (<see cref="TedideProject.ResolvedObjectDirectory"/> - every object file, assembler listing
    /// and generated assembly), the final linked output binary, the ld65 linker map, label file and
    /// debug info file, plus any .o/.lst left beside a source file by builds from before obj/
    /// existed. Skips whatever doesn't exist (e.g. a project that's never been built, or one that
    /// failed to compile some of its sources). Returns the full paths actually deleted.
    /// </summary>
    public IReadOnlyList<string> Clean(TedideProject project)
    {
        var removed = new List<string>();

        void Remove(string path)
        {
            if (!File.Exists(path))
                return;
            File.Delete(path);
            removed.Add(path);
        }

        if (Directory.Exists(project.ResolvedObjectDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(project.ResolvedObjectDirectory, "*", SearchOption.AllDirectories).ToList())
                Remove(file);
            Directory.Delete(project.ResolvedObjectDirectory, recursive: true);
        }

        // Where older builds put each source's object file and listing - removed so upgrading a
        // project doesn't leave stale copies behind. Never the source's .s counterpart: under the
        // old layout cl65 only ever deleted that, and it may well be hand-written.
        // GetFullPath normalizes a .tproj's "src/foo.c" separators, so reported paths match the OS form.
        foreach (var sourceFile in project.ResolvedSourceFiles.Select(Path.GetFullPath))
        {
            Remove(Path.ChangeExtension(sourceFile, ".o"));
            Remove(Path.ChangeExtension(sourceFile, ".lst"));
        }

        Remove(project.ResolvedOutputFile);
        Remove(project.ResolvedMapFile);
        Remove(project.ResolvedLabelsFile);
        Remove(project.ResolvedDebugInfoFile);
        return removed;
    }

    /// <summary>
    /// The cl65 invocations that turn one source file into its object file in obj/ (see
    /// <see cref="TedideProject.ResolvedObjectDirectory"/> for why there, and under that name).
    /// A C file takes two: compile to assembly with an explicit <c>-o</c> into obj/, then assemble
    /// that - never a single <c>cl65 -c foo.c</c>, which writes its intermediate foo.s beside the
    /// source and deletes it afterward, destroying any hand-written foo.s already there. An
    /// assembly file is assembled directly in one step. Each source file gets its own invocations
    /// rather than sharing one cl65 command line, because cl65/ca65 only ever write one <c>-l</c>
    /// listing per invocation.
    /// </summary>
    internal static List<List<string>> BuildCompileSteps(TedideProject project, string sourceFile)
    {
        var objectFile = RelativeToProject(project, project.ResolvedObjectFileFor(sourceFile));
        var listingFile = project.GenerateAssemblyListing
            ? RelativeToProject(project, project.ResolvedListingFileFor(sourceFile))
            : null;

        if (!TedideProject.IsCSourceFile(sourceFile))
            return [BuildAssembleArguments(project, sourceFile, objectFile, listingFile)];

        var generatedAssembly = RelativeToProject(project, project.ResolvedGeneratedAssemblyFileFor(sourceFile));
        return
        [
            BuildGenerateAssemblyArguments(project, sourceFile, generatedAssembly),
            BuildAssembleArguments(project, generatedAssembly, objectFile, listingFile),
        ];
    }

    /// <summary>
    /// The cl65 arguments to compile a C source file to assembly only (<c>-S</c>), written to
    /// <paramref name="outputFile"/> - the first of a C file's two <see cref="BuildCompileSteps"/>.
    /// Carries every compiler-level setting (optimization, <c>-T</c> source comments, include
    /// paths, defines); the listing is produced by the assemble step that follows.
    /// </summary>
    internal static List<string> BuildGenerateAssemblyArguments(TedideProject project, string sourceFile, string outputFile)
    {
        var args = new List<string>
        {
            "-t", project.Target.ToCl65Id(),
            "-S",
        };
        if (project.OptimizationLevel.ToCl65Flag() is { } optimizationFlag)
            args.Add(optimizationFlag);
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
        args.AddRange(["-o", outputFile]);
        args.Add(sourceFile);
        return args;
    }

    /// <summary>
    /// The cl65 arguments to assemble (<c>-c</c>, no link) <paramref name="inputFile"/> - either a
    /// hand-written assembly source, or the assembly <see cref="BuildGenerateAssemblyArguments"/>
    /// generated from a C file - into <paramref name="objectFile"/>, with a <c>-l</c> listing if
    /// <paramref name="listingFile"/> is given. ExtraArguments are passed here too, so an
    /// assembler-level flag in them (e.g. <c>--asm-define</c>) still reaches hand-written sources;
    /// cl65 ignores compiler-only flags when its input is already assembly.
    /// </summary>
    internal static List<string> BuildAssembleArguments(TedideProject project, string inputFile, string objectFile, string? listingFile)
    {
        var args = new List<string>
        {
            "-t", project.Target.ToCl65Id(),
            "-c",
        };
        if (project.GenerateDebugInfo)
            args.Add("-g");
        if (listingFile is not null)
            args.AddRange(["-l", listingFile]);
        args.AddRange(project.ExtraArguments);
        args.AddRange(["-o", objectFile]);
        args.Add(inputFile);
        return args;
    }

    /// <summary>obj/ paths are passed to cl65 relative to the project directory (its working
    /// directory), not absolute - the generated assembly's path is recorded as-is in the .dbg
    /// file's own file table, which should stay as portable as the source paths beside it.</summary>
    private static string RelativeToProject(TedideProject project, string path) =>
        Path.GetRelativePath(project.Directory, path);

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
