using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Tedide.Build.Opt6502;
using Tedide.Core;

namespace Tedide.Build;

/// <summary>What one <see cref="BuildStep"/> runs.</summary>
internal enum BuildTool
{
    /// <summary>cl65, as an external process.</summary>
    Cl65,

    /// <summary><see cref="Opt6502Optimizer"/>, in-process.</summary>
    Opt6502,
}

/// <summary>
/// One step of a build - see <see cref="Cc65Toolchain.BuildCompileSteps"/>. For cl65,
/// <see cref="Arguments"/> is its command line; for the optimizer, the input and output files
/// (relative to the project directory), with <see cref="Opt6502"/> its options.
/// </summary>
internal sealed record BuildStep(BuildTool Tool, List<string> Arguments, Opt6502Options? Opt6502 = null);

/// <summary>
/// Drives the cc65 toolchain (cl65) as an external process to build a <see cref="TedideProject"/>.
/// Assumes cl65 (and its ca65/ld65/co65 companions) are available on PATH, unless overridden.
/// Projects with <see cref="TedideProject.UseOpt6502"/> on also have each C file's generated
/// assembly optimized, in-process, by <see cref="Opt6502Optimizer"/>.
/// </summary>
public sealed class Cc65Toolchain(string cl65Path = "cl65")
{
    public string Cl65Path { get; } = cl65Path;

    /// <summary>ar65, which archives a library project's object files: beside cl65 when that's a
    /// full path, else on PATH like it.</summary>
    public string Ar65Path { get; } = Ar65PathFor(cl65Path);

    internal static string Ar65PathFor(string cl65Path) =>
        Path.IsPathRooted(cl65Path)
            ? Path.Combine(Path.GetDirectoryName(cl65Path)!, "ar65" + Path.GetExtension(cl65Path))
            : "ar65";

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
                // Kept off Tedide's own console - see RunToolAsync.
                CreateNoWindow = true,
                RedirectStandardInput = true,
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
    /// <param name="libraries">The library projects it links, already built, each before the ones
    /// it uses (see <see cref="ProjectGraph.LinkedLibraries"/>): their include paths are added to
    /// every compile, and their .lib files to the link. A library project archives its object
    /// files with ar65 instead of linking.</param>
    public async Task<BuildResult> BuildAsync(
        TedideProject project,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<TedideProject>? libraries = null)
    {
        libraries ??= [];
        var libraryIncludePaths = libraries.SelectMany(l => l.ResolvedIncludePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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

        BuildResult Finish(bool succeeded, int exitCode)
        {
            stopwatch.Stop();
            return new BuildResult(succeeded, exitCode, lines, Cc65DiagnosticParser.ParseAll(lines), stopwatch.Elapsed);
        }

        if (await RunBuildEventsAsync(project, "prebuild", project.PreBuildCommands, Capture, cancellationToken) is { } preBuildFailure)
            return Finish(false, preBuildFailure);

        // Every source file is compiled even after an earlier one fails, so a single build shows
        // every file's errors at once rather than stopping at the first - only the link step
        // (which would just cascade unrelated "undefined symbol" errors from the missing object
        // file) is skipped once any compile has failed.
        var objectFiles = new List<string>();
        var compileFailed = false;
        var lastExitCode = 0;
        var opt6502Total = Opt6502Stats.Empty;
        var opt6502Files = 0;
        foreach (var sourceFile in project.SourceFiles)
        {
            // cl65 won't create the obj/ subdirectory an -o/-l path points into.
            Directory.CreateDirectory(Path.GetDirectoryName(project.ResolvedObjectFileFor(sourceFile))!);

            foreach (var step in BuildCompileSteps(project, sourceFile, libraryIncludePaths))
            {
                int? exitCode;
                if (step.Tool == BuildTool.Opt6502)
                {
                    // Each file's savings become a readable summary line, and count toward the
                    // build total below.
                    cancellationToken.ThrowIfCancellationRequested();
                    var stats = RunOptimizer(project, step, Capture);
                    if (stats is not null)
                    {
                        opt6502Total = opt6502Total.Add(stats);
                        opt6502Files++;
                        Capture($"opt6502: {sourceFile}: {stats.Describe()}");
                    }
                    exitCode = stats is null ? 1 : 0;
                }
                else
                {
                    exitCode = await RunToolAsync(Cl65Path, step.Arguments, project.Directory, Capture,
                        $"Could not launch '{Cl65Path}'. Is cc65 installed and on PATH?", cancellationToken);
                }

                if (exitCode is null)
                {
                    return Finish(false, -1);
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

        if (opt6502Files > 1)
            Capture($"opt6502: total for {opt6502Files} C files: {opt6502Total.Describe()}");

        if (!compileFailed && project.IsLibrary)
        {
            // ar65 r adds to an existing archive - start from nothing so a module removed from the
            // project doesn't linger in the .lib.
            File.Delete(project.ResolvedOutputFile);
            var exitCode = await RunToolAsync(Ar65Path, BuildArchiveArguments(project, objectFiles), project.Directory, Capture,
                $"ar65: Error: Could not launch '{Ar65Path}'. Is cc65 installed and on PATH?", cancellationToken);
            if (exitCode is null)
                return Finish(false, -1);
            lastExitCode = exitCode.Value;
        }
        else if (!compileFailed)
        {
            var exitCode = await RunToolAsync(Cl65Path, BuildLinkArguments(project, objectFiles, libraries), project.Directory, Capture,
                $"Could not launch '{Cl65Path}'. Is cc65 installed and on PATH?", cancellationToken);
            if (exitCode is null)
            {
                return Finish(false, -1);
            }
            lastExitCode = exitCode.Value;
        }

        var linked = !compileFailed && lastExitCode == 0 && Cc65DiagnosticParser.ParseAll(lines).All(d => d.Severity != DiagnosticSeverity.Error);
        if (linked && await RunBuildEventsAsync(project, "postbuild", project.PostBuildCommands, Capture, cancellationToken) is { } postBuildFailure)
            return Finish(false, postBuildFailure);

        return Finish(linked, lastExitCode);
    }

    /// <summary>
    /// Runs a project's pre- or post-build commands in order, each through the shell in the
    /// project directory with its macros expanded (see <see cref="BuildEvents.Expand"/>), echoing
    /// each command before its output. Stops at the first failure, reporting it as a
    /// "<paramref name="kind"/>: Error:" line - which the Error List picks up - and returning its
    /// exit code (-1 if the shell couldn't be started); returns null when every command succeeded.
    /// </summary>
    private static async Task<int?> RunBuildEventsAsync(
        TedideProject project, string kind, IReadOnlyList<string> commands, Action<string?> capture, CancellationToken cancellationToken)
    {
        foreach (var command in commands.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var expanded = BuildEvents.Expand(project, command.Trim());
            capture($"{kind}> {expanded}");
            var exitCode = await RunToolAsync(ShellStartInfo(expanded), project.Directory, capture,
                $"{kind}: Error: Could not start the shell to run '{expanded}'", cancellationToken);
            if (exitCode is not 0)
            {
                if (exitCode is not null)
                    capture($"{kind}: Error: '{expanded}' failed with exit code {exitCode}.");
                return exitCode ?? -1;
            }
        }
        return null;
    }

    /// <summary>How to run one build-event command line: through cmd.exe on Windows, so built-ins
    /// (copy, del, if exist ...) and redirection work as they would typed at a prompt, and through
    /// /bin/sh elsewhere.</summary>
    internal static ProcessStartInfo ShellStartInfo(string command)
    {
        if (OperatingSystem.IsWindows())
            // /s with the whole command in one pair of quotes: cmd strips just those outer quotes and
            // runs the rest exactly as written, inner quotes included. ArgumentList would re-quote it.
            return new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{command}\"" };

        var startInfo = new ProcessStartInfo("/bin/sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    /// <summary>
    /// Runs an optimizer <see cref="BuildStep"/>: reads cc65's generated assembly, optimizes it and
    /// writes the result where the assemble step expects it. Returns what was done, or null - after
    /// reporting an <c>opt6502: Error:</c> line, which <see cref="Cc65DiagnosticParser"/> turns into
    /// an Error List entry - if either file couldn't be read or written. Latin-1 round-trips every
    /// byte, so nothing in the source is altered by decoding it.
    /// </summary>
    private static Opt6502Stats? RunOptimizer(TedideProject project, BuildStep step, Action<string?> capture)
    {
        var input = Path.Combine(project.Directory, step.Arguments[0]);
        var output = Path.Combine(project.Directory, step.Arguments[1]);
        try
        {
            var result = Opt6502Optimizer.Optimize(File.ReadAllText(input, Encoding.Latin1), step.Opt6502!);
            File.WriteAllText(output, result.Output, Encoding.Latin1);
            return result.Stats;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            capture($"opt6502: Error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Runs one cl65 invocation to completion, streaming its stdout/stderr lines
    /// to <paramref name="capture"/> as they arrive. Returns its exit code, or null if the process
    /// couldn't even be started (e.g. cl65 isn't on PATH) - distinct from a normal nonzero exit
    /// code, since the caller should give up immediately rather than trying further invocations
    /// against a missing tool. <paramref name="launchFailureHint"/> is shown after the OS's own
    /// reason in that case.
    /// </summary>
    private static Task<int?> RunToolAsync(
        string executable,
        List<string> arguments,
        string workingDirectory,
        Action<string?> capture,
        string launchFailureHint,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable);
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);
        return RunToolAsync(startInfo, workingDirectory, capture, launchFailureHint, cancellationToken);
    }

    /// <summary>The same as the overload above, for a process whose command line is already set up
    /// - see <see cref="ShellStartInfo"/>.</summary>
    private static async Task<int?> RunToolAsync(
        ProcessStartInfo startInfo,
        string workingDirectory,
        Action<string?> capture,
        string launchFailureHint,
        CancellationToken cancellationToken)
    {
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        // A console child shares Tedide's own console unless told otherwise, and cmd.exe resets
        // that console's input mode as it runs - measured: 0x0298 (VT input, mouse) became 0x028F
        // (line input + echo, no mouse), after which every click in the editor arrived as a raw
        // escape sequence and was typed into the file. A console of its own (hidden) and no
        // access to Tedide's stdin keep any tool from touching it.
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;

        Process? started;
        try
        {
            started = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");
        }
        catch (Win32Exception ex)
        {
            capture($"{launchFailureHint} ({ex.Message})");
            return null;
        }

        using var process = started;
        // Nothing is ever typed to a tool: closing its input straight away means one that waits
        // for a keypress (a "pause" in a build event) ends instead of hanging the build.
        process.StandardInput.Close();
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
    /// Deletes build artifacts without invoking cl65: every object file, assembler listing and
    /// generated assembly in obj/ (<see cref="TedideProject.ResolvedObjectDirectory"/> - only those,
    /// see <see cref="BuildOutputExtensions"/>, with folders left empty removed), the final linked output binary, the ld65 linker map, label file and
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
            // Only what a build writes there (.o, generated .s, .lst) - not the whole folder: a
            // project may already have had an obj/ of its own with something else in it, and Clean
            // mustn't silently destroy that. Folders left empty afterward are removed.
            foreach (var file in Directory.EnumerateFiles(project.ResolvedObjectDirectory, "*", SearchOption.AllDirectories).ToList())
            {
                if (BuildOutputExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    Remove(file);
            }
            RemoveEmptyDirectories(project.ResolvedObjectDirectory);
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

    /// <summary>What a build writes into obj/ - see <see cref="BuildCompileSteps"/>.</summary>
    private static readonly string[] BuildOutputExtensions = [".o", ".s", ".lst"];

    /// <summary>Deletes <paramref name="directory"/> and its subdirectories bottom-up, but only
    /// the ones that are (by then) empty - anything still holding a file stays.</summary>
    private static void RemoveEmptyDirectories(string directory)
    {
        foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            RemoveEmptyDirectories(subdirectory);
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
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
    /// With <see cref="TedideProject.UseOpt6502"/> on, a C file takes three: cc65's assembly goes
    /// to obj/foo.c.cc65.s instead (<see cref="TedideProject.ResolvedUnoptimizedAssemblyFileFor"/>),
    /// the optimizer writes its optimized version to the usual obj/foo.c.s, and that's what's
    /// assembled - so the listing, the .dbg file and the debugger's generated-assembly view all see
    /// the code that actually runs, at the same path as without it.
    /// </summary>
    internal static List<BuildStep> BuildCompileSteps(TedideProject project, string sourceFile, IReadOnlyList<string>? libraryIncludePaths = null)
    {
        var objectFile = RelativeToProject(project, project.ResolvedObjectFileFor(sourceFile));
        var listingFile = project.GenerateAssemblyListing
            ? RelativeToProject(project, project.ResolvedListingFileFor(sourceFile))
            : null;
        var generatedAssembly = RelativeToProject(project, project.ResolvedGeneratedAssemblyFileFor(sourceFile));
        var unoptimizedAssembly = RelativeToProject(project, project.ResolvedUnoptimizedAssemblyFileFor(sourceFile));
        // A library's code is debugged from the program that links it, whose .dbg file names
        // sources as cl65 was given them - and resolves them against the program's own folder. A
        // full path resolves from anywhere.
        if (project.IsLibrary)
            sourceFile = Path.GetFullPath(Path.Combine(project.Directory, sourceFile));

        if (!TedideProject.IsCSourceFile(sourceFile))
            return [new(BuildTool.Cl65, BuildAssembleArguments(project, sourceFile, objectFile, listingFile))];

        if (!project.UseOpt6502)
        {
            return
            [
                new(BuildTool.Cl65, BuildGenerateAssemblyArguments(project, sourceFile, generatedAssembly, libraryIncludePaths)),
                new(BuildTool.Cl65, BuildAssembleArguments(project, generatedAssembly, objectFile, listingFile)),
            ];
        }

        return
        [
            new(BuildTool.Cl65, BuildGenerateAssemblyArguments(project, sourceFile, unoptimizedAssembly, libraryIncludePaths)),
            new(BuildTool.Opt6502, [unoptimizedAssembly, generatedAssembly], BuildOpt6502Options(project)),
            new(BuildTool.Cl65, BuildAssembleArguments(project, generatedAssembly, objectFile, listingFile)),
        ];
    }

    /// <summary>
    /// The optimizer options for <paramref name="project"/>: its <see cref="TedideProject.Opt6502Mode"/>,
    /// and its CPU (see <see cref="Cc65TargetExtensions.Opt6502Cpu"/> - "65816" for a SuperCPU
    /// project, which lets the optimizer use STZ).
    /// </summary>
    internal static Opt6502Options BuildOpt6502Options(TedideProject project) =>
        new(project.Opt6502Mode, project.Target.Opt6502Cpu(project.EnableSuperCpu));

    /// <summary>
    /// The cl65 arguments to compile a C source file to assembly only (<c>-S</c>), written to
    /// <paramref name="outputFile"/> - the first of a C file's two <see cref="BuildCompileSteps"/>.
    /// Carries every compiler-level setting (optimization, <c>-T</c> source comments, include
    /// paths, defines); the listing is produced by the assemble step that follows.
    /// </summary>
    internal static List<string> BuildGenerateAssemblyArguments(TedideProject project, string sourceFile, string outputFile,
        IReadOnlyList<string>? libraryIncludePaths = null)
    {
        var args = new List<string>
        {
            "-t", project.Target.ToCl65Id(),
            // Explicit rather than left to the target's default - see TedideProject.ResolvedCc65Cpu.
            "--cpu", project.ResolvedCc65Cpu,
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
        // After the project's own, so its headers win a name clash.
        foreach (var includePath in libraryIncludePaths ?? [])
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
            // ca65 takes the same --cpu, so a hand-written source in a SuperCPU project may use
            // 65C02/65816 instructions too (a .setcpu inside the file still overrides it).
            "--cpu", project.ResolvedCc65Cpu,
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
        project.IsLibrary ? path : Path.GetRelativePath(project.Directory, path);

    /// <summary>
    /// The cl65 arguments to link a project's already-compiled object files into its output
    /// binary. Run once, after every source file has been compiled with <see cref="BuildCompileArguments"/>.
    /// Any .lib files found in the project's lib/ folder (<see cref="TedideProject.ResolvedLibFiles"/>)
    /// are appended after the object files, so ld65 resolves undefined symbols from them the same
    /// way it would object-file-then-library arguments on a hand-written command line.
    /// </summary>
    internal static List<string> BuildLinkArguments(TedideProject project, IEnumerable<string> objectFiles,
        IReadOnlyList<TedideProject>? libraries = null)
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
        // Referenced library projects before lib/'s prebuilt ones, which they may use too.
        args.AddRange((libraries ?? []).Select(l => l.ResolvedOutputFile));
        args.AddRange(project.ResolvedLibFiles);
        return args;
    }

    /// <summary>The ar65 arguments that archive a library project's object files into its .lib.</summary>
    internal static List<string> BuildArchiveArguments(TedideProject project, IEnumerable<string> objectFiles) =>
        ["r", project.ResolvedOutputFile, .. objectFiles];
}
