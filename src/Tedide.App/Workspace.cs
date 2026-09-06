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

    public TedideProject NewProject(string directory, string name, Cc65Target target)
    {
        Directory.CreateDirectory(directory);

        var mainSourceName = "main.c";
        var mainSourcePath = Path.Combine(directory, mainSourceName);
        if (!File.Exists(mainSourcePath))
            File.WriteAllText(mainSourcePath, SampleMainC);

        var project = new TedideProject
        {
            Name = name,
            Target = target,
            SourceFiles = [mainSourceName],
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

    private const string SampleMainC = """
        #include <stdio.h>
        #include <conio.h>

        int main(void)
        {
            clrscr();
            cprintf("Hello from Tedide!\r\n");
            cgetc();
            return 0;
        }

        """;
}
