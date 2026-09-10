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

    /// <summary>Path this project was loaded from / will be saved to. Not serialized.</summary>
    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public string Directory => FilePath is null
        ? System.Environment.CurrentDirectory
        : (Path.GetDirectoryName(Path.GetFullPath(FilePath)) ?? System.Environment.CurrentDirectory);

    [JsonIgnore]
    public string ResolvedOutputFile =>
        Path.Combine(Directory, OutputFile ?? (Name + Target.DefaultOutputExtension()));

    /// <summary>
    /// Where cl65's -l assembler listings are written when <see cref="GenerateAssemblyListing"/>
    /// is on - one per source file, each source file's own path with a .lst extension (e.g.
    /// src/main.c -> src/main.lst), the same way its .o object file is placed. Each project source
    /// is compiled in its own cl65 invocation specifically so this can be one listing per source
    /// file rather than a single listing covering the whole project.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<string> ResolvedListingFiles => ResolvedSourceFiles.Select(f => Path.ChangeExtension(f, ".lst"));

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
