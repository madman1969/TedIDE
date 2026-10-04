using Tedide.Core;

namespace Tedide.App;

/// <summary>
/// Tracks the currently loaded solution/project(s) in the running IDE session.
/// A bare .tproj can be opened without a .tsln - it is then the sole project in an
/// unsaved, in-memory solution wrapper.
/// </summary>
public sealed class Workspace
{
    public TedideSolution? Solution { get; private set; }
    public List<TedideProject> Projects { get; } = [];

    public event Action? Changed;

    /// <summary>
    /// The startup project: what Run and Start Debugging launch, Build Project builds, and whose
    /// breakpoints, session and symbols are shown. The solution's <see cref="TedideSolution.StartupProject"/>
    /// if it names one of <see cref="Projects"/>, else the first application, else the first project.
    /// </summary>
    public TedideProject? ActiveProject =>
        (Solution?.StartupProject is { } startup ? ProjectGraph.Find(Projects, Path.Combine(Solution.Directory, startup)) : null)
        ?? Projects.FirstOrDefault(p => !p.IsLibrary)
        ?? Projects.FirstOrDefault();

    /// <summary>Makes <paramref name="project"/> the startup project, saving the choice in the solution file.</summary>
    public void SetStartupProject(TedideProject project)
    {
        if (Solution is null || project.FilePath is null)
            return;
        Solution.StartupProject = Path.GetRelativePath(Solution.Directory, project.FilePath);
        Solution.Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// The project <paramref name="filePath"/> (a file or folder) belongs to: the one whose folder
    /// holds it - the innermost, should one project sit inside another's folder - else one that
    /// uses it from a <see cref="TedideProject.LinkedDirectories">linked folder</see>, or null.
    /// Several projects can share a linked folder; the startup project is preferred, then a
    /// library it links (so a shared source is seen for the target being run), then the first.
    /// </summary>
    public TedideProject? ProjectFor(string filePath)
    {
        if (Projects.Where(p => IsInside(filePath, p.Directory)).OrderByDescending(p => p.Directory.Length).FirstOrDefault() is { } owner)
            return owner;

        var linking = Projects.Where(p => p.LinkedDirectories.Any(d => IsInside(filePath, d))).ToList();
        if (linking.Count <= 1 || ActiveProject is not { } active)
            return linking.FirstOrDefault();
        if (linking.Contains(active))
            return active;
        return linking.FirstOrDefault(p => ProjectGraph.DependsOn(Projects, active, p)) ?? linking[0];
    }

    /// <summary>The macros that decide which <c>#if</c> branches count in <paramref name="path"/>: its
    /// project's target's own and -D defines (else the startup project's).</summary>
    public IReadOnlyList<string> MacrosFor(string? path)
    {
        var project = (path is null ? null : ProjectFor(path)) ?? ActiveProject;
        return project is null ? [] : [.. project.Target.PredefinedMacros(), .. project.PreprocessorDefines.Select(d => d.Split('=', 2)[0].Trim())];
    }

    private static bool IsInside(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>
    /// Scaffolds a new project in <paramref name="directory"/> using the same src/include/lib/bin
    /// layout as the bundled samples (see "Project files" in the README) - GitHub's most common
    /// C project layout, plus lib/ - rather than a flat directory with main.c and the output binary
    /// sitting next to the .tproj. include/ gets a starter main.h (paired with src/main.c the same
    /// way e.g. HelloCBM's screen.c/screen.h are) rather than sitting empty, so the include/ folder
    /// - and the -I include that finds it - are exercised by a real, working #include from the
    /// moment the project is created, not just present but unused; bin/ is deliberately left for
    /// the first build to create, same as the samples (see Cc65Toolchain.BuildAsync). lib/ is
    /// created empty - dropping a prebuilt cc65 .lib archive into it is enough to link against it,
    /// no .tproj change needed (see TedideProject.ResolvedLibFiles). Also creates a same-named
    /// .tsln wrapping the new .tproj, right beside it, the same way every bundled sample is wrapped
    /// - so the freshly-created project shows up in the Solution Explorer, Recent Projects, etc.
    /// exactly like one you'd open via "samples/Name/Name.tsln", not as a bare project some other
    /// path through the app happens to leave unwrapped.
    /// </summary>
    public TedideProject NewProject(string directory, string name, Cc65Target target, ProjectOutputType outputType = ProjectOutputType.Application)
    {
        var project = ScaffoldProject(directory, name, target, outputType);

        // A flat filename reference, not Path.GetRelativePath/AddProject - the project always
        // sits directly beside its new solution here, exactly like every bundled sample's own
        // hand-authored .tsln (e.g. HelloCBM.tsln -> "HelloCBM.tproj").
        var solution = new TedideSolution
        {
            Name = name,
            ProjectPaths = [name + TedideProject.FileExtension],
        };
        solution.Save(Path.Combine(directory, name + TedideSolution.FileExtension));

        Solution = solution;
        Projects.Clear();
        Projects.Add(project);
        Changed?.Invoke();
        return project;
    }

    /// <summary>
    /// Solution Explorer > Add New Project: scaffolds a project (see <see cref="NewProject"/>, minus
    /// its own .tsln) and adds it to the open solution, which is saved.
    /// </summary>
    public TedideProject AddNewProject(string directory, string name, Cc65Target target, ProjectOutputType outputType)
    {
        var solution = Solution ?? throw new InvalidOperationException("Open a solution (.tsln) to add a project to it.");
        if (File.Exists(Path.Combine(directory, name + TedideProject.FileExtension)))
            throw new IOException($"{name}{TedideProject.FileExtension} already exists in {directory}.");
        var project = ScaffoldProject(directory, name, target, outputType);
        solution.AddProject(project);
        solution.Save();
        Projects.Add(project);
        Changed?.Invoke();
        return project;
    }

    /// <summary>
    /// Where Add New Project puts a new project's folder by default: beside the solution file - or,
    /// when the solution file sits in a project's own folder (every sample's does: HelloCBM.tsln
    /// beside HelloCBM.tproj), beside that project, so the new one is its sibling rather than
    /// nested inside it.
    /// </summary>
    public string DefaultNewProjectParent()
    {
        if (Solution is not { } solution)
            return System.Environment.CurrentDirectory;
        var owner = Projects
            .Where(p => IsInside(solution.Directory, p.Directory))
            .OrderBy(p => p.Directory.Length)
            .FirstOrDefault();
        return owner is not null && Path.GetDirectoryName(owner.Directory) is { } parent ? parent : solution.Directory;
    }

    /// <summary>Solution Explorer > Add Existing Project: adds a .tproj from disk to the open solution, which is saved.</summary>
    public TedideProject AddExistingProject(string projectFile)
    {
        var solution = Solution ?? throw new InvalidOperationException("Open a solution (.tsln) to add a project to it.");
        if (ProjectGraph.Find(Projects, projectFile) is not null)
            throw new InvalidDataException($"{Path.GetFileName(projectFile)} is already in the solution.");
        var project = TedideProject.Load(projectFile);
        solution.AddProject(project);
        solution.Save();
        Projects.Add(project);
        Changed?.Invoke();
        return project;
    }

    /// <summary>
    /// Solution Explorer > Remove from Solution: drops the project from the solution (its files
    /// stay on disk) and every other project's reference to it. Everything changed is saved.
    /// </summary>
    public void RemoveProject(TedideProject project)
    {
        if (Solution is null || project.FilePath is null)
            return;
        Solution.RemoveProject(project);
        Projects.Remove(project);
        foreach (var other in Projects.Where(p => p.RetargetReference(project.FilePath, null)))
            other.Save();
        Solution.Save();
        Changed?.Invoke();
    }

    /// <summary>After a project's .tproj moved (a rename), points other projects' references at
    /// its new path, saving each one changed.</summary>
    public void RetargetReferences(string oldProjectFile, string newProjectFile)
    {
        foreach (var other in Projects.Where(p => p.RetargetReference(oldProjectFile, newProjectFile)))
            other.Save();
    }

    /// <summary>The src/include/lib layout every project gets - see <see cref="NewProject"/>.</summary>
    private static TedideProject ScaffoldProject(string directory, string name, Cc65Target target, ProjectOutputType outputType)
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        Directory.CreateDirectory(Path.Combine(directory, "include"));
        Directory.CreateDirectory(Path.Combine(directory, "lib"));

        // Relative paths stored in the .tproj use "/" literally (not Path.Combine) to match the
        // bundled samples' own .tproj files, which are portable, human-authored path strings
        // rather than filesystem paths - cl65 and Path.Combine both accept "/" fine on Windows.
        if (outputType == ProjectOutputType.Library)
            return ScaffoldLibrary(directory, name, target);

        const string mainSourceName = "src/main.c";
        var mainSourcePath = Path.Combine(directory, "src", "main.c");
        if (!File.Exists(mainSourcePath))
            File.WriteAllText(mainSourcePath, SampleMainC);

        // main.c pulls in conio.h/stdio.h via this header rather than #include-ing them directly,
        // so the new project has a real, immediately-working example of a project-local header -
        // resolved through -I include, above, exactly like the bundled samples' own headers are -
        // rather than an include/ folder that sits empty until the user adds their own.
        var mainHeaderPath = Path.Combine(directory, "include", "main.h");
        if (!File.Exists(mainHeaderPath))
            File.WriteAllText(mainHeaderPath, SampleMainH);

        var project = new TedideProject
        {
            Name = name,
            Target = target,
            SourceFiles = [mainSourceName],
            OutputFile = $"bin/{name}{target.DefaultOutputExtension()}",
            IncludePaths = ["include"],
        };
        project.Save(Path.Combine(directory, name + TedideProject.FileExtension));
        return project;
    }

    /// <summary>
    /// A library project: one source file with a function, and the header that declares it - named
    /// after the project, so a program referencing two libraries doesn't get two "library.h"s.
    /// </summary>
    private static TedideProject ScaffoldLibrary(string directory, string name, Cc65Target target)
    {
        var identifier = CIdentifier(name);
        var sourceName = $"src/{identifier}.c";
        var sourcePath = Path.Combine(directory, "src", identifier + ".c");
        if (!File.Exists(sourcePath))
            File.WriteAllText(sourcePath, SampleLibraryC.Replace("NAME", identifier));
        var headerPath = Path.Combine(directory, "include", identifier + ".h");
        if (!File.Exists(headerPath))
            File.WriteAllText(headerPath, SampleLibraryH.Replace("NAME_H", identifier.ToUpperInvariant() + "_H").Replace("NAME", identifier));

        var project = new TedideProject
        {
            Name = name,
            Target = target,
            OutputType = ProjectOutputType.Library,
            SourceFiles = [sourceName],
            OutputFile = $"bin/{name}{TedideProject.LibraryExtension}",
            IncludePaths = ["include"],
        };
        project.Save(Path.Combine(directory, name + TedideProject.FileExtension));
        return project;
    }

    /// <summary>A project name as a C identifier, for the library template's file and function
    /// names: lower case, anything else an underscore, and never starting with a digit.</summary>
    internal static string CIdentifier(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        var identifier = new string(chars);
        return identifier.Length == 0 || char.IsAsciiDigit(identifier[0]) ? "_" + identifier : identifier;
    }

    public TedideProject OpenProject(string tprojPath)
    {
        var project = TedideProject.Load(tprojPath);
        Solution = null;
        Projects.Clear();
        Projects.Add(project);
        Changed?.Invoke();
        return project;
    }

    public TedideSolution OpenSolution(string tslnPath)
    {
        // Everything loaded before anything is replaced: a missing or corrupt .tproj throws here,
        // and must leave the previously open solution intact rather than half-swapped.
        var solution = TedideSolution.Load(tslnPath);
        var projects = solution.LoadProjects();
        Solution = solution;
        Projects.Clear();
        Projects.AddRange(projects);
        Changed?.Invoke();
        return solution;
    }

    /// <summary>Saves the solution and projects that have changed since they were loaded or last
    /// saved - see <see cref="TedideProject.SaveIfChanged"/>. Unchanged files aren't touched.</summary>
    public void SaveAll()
    {
        Solution?.SaveIfChanged();
        foreach (var project in Projects)
            project.SaveIfChanged();
    }

    /// <summary>Discards the currently loaded solution/project(s) from this session. Files already saved to disk are untouched.</summary>
    public void Close()
    {
        Solution = null;
        Projects.Clear();
        Changed?.Invoke();
    }

    private const string SampleMainC = """
        #include "main.h"

        int main(void)
        {
            clrscr();
            cprintf("Hello from Tedide!\r\n");
            cgetc();
            return 0;
        }

        """;

    private const string SampleLibraryC = """
        #include "NAME.h"

        unsigned char NAME_add(unsigned char a, unsigned char b)
        {
            return a + b;
        }

        """;

    private const string SampleLibraryH = """
        #ifndef NAME_H
        #define NAME_H

        /* Declarations for the functions in this library. A project that references it finds
           this header through the library's include paths. */
        unsigned char NAME_add(unsigned char a, unsigned char b);

        #endif

        """;

    private const string SampleMainH = """
        #ifndef MAIN_H
        #define MAIN_H

        #include <stdio.h>
        #include <conio.h>

        #endif

        """;
}
