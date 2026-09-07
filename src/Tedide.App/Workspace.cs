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

    public TedideProject? ActiveProject => Projects.Count > 0 ? Projects[0] : null;

    /// <summary>
    /// Scaffolds a new project in <paramref name="directory"/> using the same src/include/bin
    /// layout as the bundled samples (see "Project files" in the README) - GitHub's most common
    /// C project layout - rather than a flat directory with main.c and the output binary sitting
    /// next to the .tproj. include/ gets a starter main.h (paired with src/main.c the same way
    /// e.g. HelloCBM's screen.c/screen.h are) rather than sitting empty, so the include/ folder -
    /// and the -I include that finds it - are exercised by a real, working #include from the
    /// moment the project is created, not just present but unused; bin/ is deliberately left for
    /// the first build to create, same as the samples (see Cc65Toolchain.BuildAsync).
    /// </summary>
    public TedideProject NewProject(string directory, string name, Cc65Target target)
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        Directory.CreateDirectory(Path.Combine(directory, "include"));

        // Relative paths stored in the .tproj use "/" literally (not Path.Combine) to match the
        // bundled samples' own .tproj files, which are portable, human-authored path strings
        // rather than filesystem paths - cl65 and Path.Combine both accept "/" fine on Windows.
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
            ExtraArguments = ["-I", "include"],
        };
        project.Save(Path.Combine(directory, name + TedideProject.FileExtension));

        Solution = null;
        Projects.Clear();
        Projects.Add(project);
        Changed?.Invoke();
        return project;
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
        var solution = TedideSolution.Load(tslnPath);
        Solution = solution;
        Projects.Clear();
        Projects.AddRange(solution.LoadProjects());
        Changed?.Invoke();
        return solution;
    }

    public void SaveAll()
    {
        Solution?.Save();
        foreach (var project in Projects)
            project.Save();
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

    private const string SampleMainH = """
        #ifndef MAIN_H
        #define MAIN_H

        #include <stdio.h>
        #include <conio.h>

        #endif

        """;
}
