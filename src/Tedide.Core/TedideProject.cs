using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tedide.Core;

/// <summary>
/// The on-disk model for a .tproj project file: a cc65-buildable set of sources
/// plus the target platform and any extra cl65 arguments.
/// </summary>
public sealed class TedideProject
{
    public const string FileExtension = ".tproj";

    public string Name { get; set; } = "NewProject";

    public Cc65Target Target { get; set; } = Cc65Target.C64;

    /// <summary>A program (linked into <see cref="ResolvedOutputFile"/>), or a library (its object
    /// files archived into a .lib by ar65, for projects that reference it to link against).</summary>
    public ProjectOutputType OutputType { get; set; } = ProjectOutputType.Application;

    /// <summary>
    /// The library projects this one links against, as paths to their .tproj files relative to this
    /// project's directory. They must be in the same solution: they build first, their include
    /// paths are added to this project's, and their .lib files are linked in - see
    /// <see cref="ProjectGraph"/>.
    /// </summary>
    public List<string> ProjectReferences { get; set; } = [];

    /// <summary>The cc65 compiler optimization preset to build with. Defaults to no optimization.</summary>
    public Cc65OptimizationLevel OptimizationLevel { get; set; } = Cc65OptimizationLevel.None;

    /// <summary>Whether cl65 should emit an assembler listing file (-l) for each source file, alongside that source file. Defaults to on.</summary>
    public bool GenerateAssemblyListing { get; set; } = true;

    /// <summary>
    /// Whether cc65 should include each C source line as a comment in the assembly it generates
    /// for that file (cl65 -T / --add-source). Most useful together with
    /// <see cref="GenerateAssemblyListing"/>, which is what actually surfaces those comments to
    /// the user - it interleaves them with the generated 6502 instructions in that file's .lst.
    /// Defaults to on.
    /// </summary>
    public bool AddSourceAsComment { get; set; } = true;

    /// <summary>Whether ld65 should emit a linker map file (-m) alongside the project, showing
    /// every segment's address/size and where each object file's symbols ended up. Defaults to
    /// off - most builds don't need it. See <see cref="ResolvedMapFile"/> for its fixed name.</summary>
    public bool GenerateLinkerMap { get; set; }

    /// <summary>Whether cc65/ca65 should embed debug info (-g) and ld65 should consolidate it into
    /// a .dbg file (--dbgfile - forwarded through cl65 as "-Wl --dbgfile,path" since cl65 has no
    /// top-level flag for it, unlike -m/-Ln above) alongside the project. Defaults to off. See
    /// <see cref="ResolvedDebugInfoFile"/> for its fixed name, and Tedide.Core.Debugging.DbgFile
    /// for what this feeds - source-line <-> address resolution for the VICE debugger.</summary>
    public bool GenerateDebugInfo { get; set; }

    /// <summary>Whether ld65 should emit a VICE-format label file (-Ln) alongside the project,
    /// loadable into VICE's own monitor (or another machine-language monitor that understands the
    /// same format) to resolve addresses back to symbol names while debugging. Defaults to off.
    /// See <see cref="ResolvedLabelsFile"/> for its fixed name.</summary>
    public bool ExportLabels { get; set; }

    /// <summary>Whether Build/Run/Debug should launch this project in VICE's dedicated SuperCPU
    /// emulator (xscpu64.exe) instead of the target's normal one (see Tedide.Build's
    /// ViceEmulator.ExecutableNameFor). Only meaningful when <see cref="Target"/> is
    /// <see cref="Cc65Target.C64"/> (the SuperCPU is a C64-specific accelerator cartridge);
    /// ignored for every other target. Defaults to off.</summary>
    public bool EnableSuperCpu { get; set; }

    /// <summary>Whether each C file's cc65-generated assembly is run through Tedide's optimizer
    /// (Tedide.Build's Opt6502Optimizer, still called opt6502 after the program it replaced) before
    /// it's assembled - see Tedide.Build's Cc65Toolchain.BuildCompileSteps. Defaults to off.
    /// Hand-written assembly sources are never touched.</summary>
    public bool UseOpt6502 { get; set; }

    /// <summary>Whether opt6502 favours size or speed - see <see cref="Core.Opt6502Mode"/>. Defaults
    /// to <see cref="Opt6502Mode.Size"/>, which never makes code bigger.</summary>
    public Opt6502Mode Opt6502Mode { get; set; } = Opt6502Mode.Size;

    /// <summary>The CPU this project builds for: <see cref="Target"/>'s own processor, or the
    /// SuperCPU's 65816 when <see cref="EnableSuperCpu"/> is on for a C64 - see
    /// <see cref="Cc65TargetExtensions.Cc65Cpu"/>.</summary>
    [JsonIgnore]
    public string ResolvedCc65Cpu => Target.Cc65Cpu(EnableSuperCpu);

    /// <summary>Source file paths, relative to the project file's directory.</summary>
    public List<string> SourceFiles { get; set; } = [];

    /// <summary>Output binary name, relative to the project file's directory. Defaults to Name + target extension.</summary>
    public string? OutputFile { get; set; }

    /// <summary>Custom ld65 linker configuration file (-C), relative to the project file's
    /// directory. Null/blank means use cl65's built-in per-target default configuration - a custom
    /// config typically replaces that default rather than layering on top of it (see the ld65
    /// manual for how -C and -t interact).</summary>
    public string? LinkerConfigPath { get; set; }

    /// <summary>Directories passed to cl65 as -I include paths (compile-time only), each relative
    /// to the project file's directory. Applied to every source file compiled after them on the
    /// command line - see Cc65Toolchain.BuildCompileArguments for why they're emitted before
    /// ExtraArguments and the source file itself.</summary>
    public List<string> IncludePaths { get; set; } = [];

    /// <summary>Preprocessor defines passed to cl65 as -D flags (compile-time only), each either
    /// "NAME" or "NAME=VALUE".</summary>
    public List<string> PreprocessorDefines { get; set; } = [];

    /// <summary>Extra arguments appended verbatim to the cl65 command line.</summary>
    public List<string> ExtraArguments { get; set; } = [];

    /// <summary>Shell commands run, in order, in the project's directory before anything is
    /// compiled; the build stops if one fails. <see cref="BuildEvents.Expand"/> substitutes
    /// $(OutputFile) and friends first.</summary>
    public List<string> PreBuildCommands { get; set; } = [];

    /// <summary>Shell commands run, in order, in the project's directory after a successful link -
    /// e.g. c1541 to put the program on a .d64 disk image. A failing one fails the build. See
    /// <see cref="BuildEvents.Expand"/> for the macros they can use.</summary>
    public List<string> PostBuildCommands { get; set; } = [];

    /// <summary>Path this project was loaded from / will be saved to. Not serialized.</summary>
    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public string Directory => FilePath is null
        ? System.Environment.CurrentDirectory
        : (Path.GetDirectoryName(Path.GetFullPath(FilePath)) ?? System.Environment.CurrentDirectory);

    [JsonIgnore]
    public bool IsLibrary => OutputType == ProjectOutputType.Library;

    [JsonIgnore]
    public string ResolvedOutputFile =>
        Path.Combine(Directory, OutputFile ?? (Name + DefaultOutputExtension));

    /// <summary>".lib" for a library, else the target's own program extension.</summary>
    [JsonIgnore]
    public string DefaultOutputExtension => IsLibrary ? LibraryExtension : Target.DefaultOutputExtension();

    public const string LibraryExtension = ".lib";

    /// <summary>Absolute paths of the .tproj files in <see cref="ProjectReferences"/>.</summary>
    [JsonIgnore]
    public IEnumerable<string> ResolvedProjectReferences =>
        ProjectReferences.Select(r => Path.GetFullPath(Path.Combine(Directory, r)));

    /// <summary>Whether this project references the project file at <paramref name="projectFile"/>.</summary>
    public bool References(string projectFile) =>
        ResolvedProjectReferences.Any(r => string.Equals(r, Path.GetFullPath(projectFile), StringComparison.OrdinalIgnoreCase));

    /// <summary>Points any reference to <paramref name="oldProjectFile"/> at <paramref name="newProjectFile"/>
    /// (after a rename), or drops it if <paramref name="newProjectFile"/> is null (after a removal).
    /// True if anything changed.</summary>
    public bool RetargetReference(string oldProjectFile, string? newProjectFile)
    {
        var index = ProjectReferences.FindIndex(r =>
            string.Equals(Path.GetFullPath(Path.Combine(Directory, r)), Path.GetFullPath(oldProjectFile), StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;
        if (newProjectFile is null)
            ProjectReferences.RemoveAt(index);
        else
            ProjectReferences[index] = Path.GetRelativePath(Directory, newProjectFile);
        return true;
    }

    /// <summary>Absolute paths of <see cref="IncludePaths"/> - what a project referencing this
    /// library adds to its own <c>-I</c> list, so the library's headers are found from there.</summary>
    [JsonIgnore]
    public IEnumerable<string> ResolvedIncludePaths =>
        IncludePaths.Select(i => Path.GetFullPath(Path.Combine(Directory, i)));

    /// <summary>
    /// Where every per-source build output goes: "obj/" in the project's own directory, mirroring
    /// each source file's relative path with an extra extension appended to the *full* file name
    /// (src/main.c -> obj/src/main.c.o, src/border.s -> obj/src/border.s.o) rather than replacing
    /// its extension. Both halves of that matter: cl65 writes the intermediate assembly for a C
    /// file next to wherever that assembly is going, so building src/foo.c straight to src/foo.o
    /// overwrote and then deleted a hand-written src/foo.s beside it (confirmed against a real
    /// cl65 2.19); and keeping the source's own extension in the name stops foo.c and foo.s from
    /// both compiling to the same foo.o/foo.lst. Clean removes only those build
    /// outputs (.o, .s, .lst) from it, so anything else placed in obj/ is left alone.
    /// </summary>
    [JsonIgnore]
    public string ResolvedObjectDirectory => Path.Combine(Directory, "obj");

    /// <summary>The object file <paramref name="sourceFile"/> (relative, as in <see cref="SourceFiles"/>)
    /// compiles to - see <see cref="ResolvedObjectDirectory"/>.</summary>
    public string ResolvedObjectFileFor(string sourceFile) => ResolvedIntermediateFileFor(sourceFile, ".o");

    /// <summary>The assembler listing <paramref name="sourceFile"/> gets when <see cref="GenerateAssemblyListing"/>
    /// is on - see <see cref="ResolvedObjectDirectory"/>.</summary>
    public string ResolvedListingFileFor(string sourceFile) => ResolvedIntermediateFileFor(sourceFile, ".lst");

    /// <summary>The assembly cc65 generates from <paramref name="sourceFile"/> on the way to its
    /// object file - only meaningful for a C source (see <see cref="IsCSourceFile"/>); a
    /// hand-written assembly source is assembled directly and has no generated counterpart.</summary>
    public string ResolvedGeneratedAssemblyFileFor(string sourceFile) => ResolvedIntermediateFileFor(sourceFile, ".s");

    /// <summary>With <see cref="UseOpt6502"/> on, where cc65's own assembly for
    /// <paramref name="sourceFile"/> goes (obj/src/main.c.cc65.s) - opt6502 then writes its
    /// optimized version to <see cref="ResolvedGeneratedAssemblyFileFor"/>, so the file that's
    /// assembled, listed and named in the .dbg file keeps the same path either way.</summary>
    public string ResolvedUnoptimizedAssemblyFileFor(string sourceFile) => ResolvedIntermediateFileFor(sourceFile, ".cc65.s");

    /// <summary>Whether <paramref name="sourceFile"/> is C (compiled to assembly first, then
    /// assembled) rather than assembly (assembled directly).</summary>
    public static bool IsCSourceFile(string sourceFile) =>
        string.Equals(Path.GetExtension(sourceFile), ".c", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every source file's assembler listing path (see <see cref="ResolvedListingFileFor"/>),
    /// in <see cref="SourceFiles"/> order. Each project source is compiled in its own cl65
    /// invocation specifically so this can be one listing per source file rather than a single
    /// listing covering the whole project.</summary>
    [JsonIgnore]
    public IEnumerable<string> ResolvedListingFiles => SourceFiles.Select(ResolvedListingFileFor);

    private string ResolvedIntermediateFileFor(string sourceFile, string extension)
    {
        // Normally just "src/main.c", but a source outside the project directory ("../shared/x.c",
        // or another drive entirely) must still land *inside* obj/ - ".." segments and a drive
        // root are replaced rather than followed.
        var relative = Path.GetRelativePath(Directory, Path.Combine(Directory, sourceFile));
        if (Path.IsPathRooted(relative))
            relative = Path.Combine("_root", relative[Path.GetPathRoot(relative)!.Length..]);
        var segments = relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Select(s => s == ".." ? "__" : s);
        return Path.Combine([ResolvedObjectDirectory, .. segments]) + extension;
    }

    /// <summary>Where ld65's -m linker map is written when <see cref="GenerateLinkerMap"/> is on -
    /// always "lnk.map" directly in the project's own directory, not per-source like the .lst
    /// listings above (a link is one invocation covering the whole project, not one per source
    /// file).</summary>
    [JsonIgnore]
    public string ResolvedMapFile => Path.Combine(Directory, "lnk.map");

    /// <summary>Where ld65's -Ln label file is written when <see cref="ExportLabels"/> is on -
    /// "{Name}.lbl" in the project's own directory, matching how <see cref="ResolvedOutputFile"/>
    /// defaults to "{Name}" plus a target-specific extension.</summary>
    [JsonIgnore]
    public string ResolvedLabelsFile => Path.Combine(Directory, Name + ".lbl");

    /// <summary>Where ld65's --dbgfile debug info is written when <see cref="GenerateDebugInfo"/>
    /// is on - "{Name}.dbg" in the project's own directory, matching <see cref="ResolvedLabelsFile"/>'s convention.</summary>
    [JsonIgnore]
    public string ResolvedDebugInfoFile => Path.Combine(Directory, Name + ".dbg");

    /// <summary>Where this project's breakpoints are stored - see <see cref="BreakpointsFile"/> for
    /// why they're a separate sidecar file rather than a field on this class.</summary>
    [JsonIgnore]
    public string ResolvedBreakpointsFile => Path.Combine(Directory, Name + ".breakpoints.json");

    /// <summary>Where this project's editor session state (currently just the last open file) is
    /// stored - see <see cref="SessionStateFile"/> for why it's a separate sidecar file rather than
    /// a field on this class.</summary>
    [JsonIgnore]
    public string ResolvedSessionFile => Path.Combine(Directory, Name + ".session.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TedideProject Load(string path)
    {
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<TedideProject>(json, JsonOptions)
            ?? throw new InvalidDataException($"Could not parse project file '{path}'.");
        project.FilePath = Path.GetFullPath(path);
        return project;
    }

    public void Save(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("No path specified and project has no FilePath.");
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
        FilePath = Path.GetFullPath(path);
    }

    /// <summary>Absolute paths of all source files.</summary>
    [JsonIgnore]
    public IEnumerable<string> ResolvedSourceFiles => SourceFiles.Select(f => Path.Combine(Directory, f));

    /// <summary>
    /// Absolute paths of every .lib file directly inside the project's lib/ folder (not
    /// recursive), sorted for a deterministic link command line. Every project gets an empty lib/
    /// folder (see NewProject in Tedide.App's Workspace, and every bundled sample), so a prebuilt
    /// cc65 library archive can be linked in by just dropping it there - no .tproj change needed;
    /// see Tedide.Build's Cc65Toolchain.BuildLinkArguments for where these are actually passed to
    /// ld65, alongside the compiled object files. Empty if lib/ doesn't exist or has no .lib files.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<string> ResolvedLibFiles
    {
        get
        {
            var libDirectory = Path.Combine(Directory, "lib");
            return System.IO.Directory.Exists(libDirectory)
                ? System.IO.Directory.EnumerateFiles(libDirectory, "*.lib").OrderBy(f => f, StringComparer.Ordinal)
                : [];
        }
    }
}
