namespace Tedide.Core;

/// <summary>
/// How a solution's projects depend on each other through <see cref="TedideProject.ProjectReferences"/>:
/// the order to build them in, which libraries each one links, and which references would be
/// invalid. A reference must name a library project in the same solution, and references can't
/// form a cycle; anything else is an <see cref="InvalidDataException"/> naming the problem.
/// </summary>
public static class ProjectGraph
{
    /// <summary>
    /// <paramref name="roots"/> and every library they reference, directly or not, each after the
    /// libraries it references - the order to build them in. Projects keep their solution order
    /// wherever the references leave a choice.
    /// </summary>
    public static IReadOnlyList<TedideProject> BuildOrder(IReadOnlyList<TedideProject> solution, IEnumerable<TedideProject> roots)
    {
        var order = new List<TedideProject>();
        var state = new Dictionary<TedideProject, bool>(); // false: being visited, true: done
        var rootSet = roots.ToHashSet();
        foreach (var project in solution.Where(rootSet.Contains))
            Visit(solution, project, state, order, []);
        return order;
    }

    /// <summary>
    /// The libraries <paramref name="project"/> links, directly or through other libraries, with
    /// each library before the ones it uses - the order ld65 needs, since it only looks forward
    /// through the library list for a symbol a library still needs.
    /// </summary>
    public static IReadOnlyList<TedideProject> LinkedLibraries(IReadOnlyList<TedideProject> solution, TedideProject project)
    {
        var order = BuildOrder(solution, [project]).Where(p => p != project).ToList();
        order.Reverse();
        return order;
    }

    /// <summary>The libraries <paramref name="project"/> references directly, in its own order.</summary>
    public static IReadOnlyList<TedideProject> DirectReferences(IReadOnlyList<TedideProject> solution, TedideProject project) =>
        project.ResolvedProjectReferences.Select(path => Resolve(solution, project, path)).ToList();

    /// <summary>
    /// The library projects <paramref name="project"/> could reference without creating a cycle:
    /// every other library in the solution that doesn't itself (even indirectly) reference it.
    /// </summary>
    public static IReadOnlyList<TedideProject> ReferenceCandidates(IReadOnlyList<TedideProject> solution, TedideProject project) =>
        solution.Where(p => p != project && p.IsLibrary && !DependsOn(solution, p, project)).ToList();

    /// <summary>Whether <paramref name="project"/> references <paramref name="other"/>, directly or not.
    /// Unresolvable references are ignored here.</summary>
    public static bool DependsOn(IReadOnlyList<TedideProject> solution, TedideProject project, TedideProject other)
    {
        var seen = new HashSet<TedideProject>();
        var pending = new Stack<TedideProject>([project]);
        while (pending.TryPop(out var current))
        {
            foreach (var path in current.ResolvedProjectReferences)
            {
                if (Find(solution, path) is not { } referenced || !seen.Add(referenced))
                    continue;
                if (referenced == other)
                    return true;
                pending.Push(referenced);
            }
        }
        return false;
    }

    /// <summary>The project in <paramref name="solution"/> whose .tproj is at <paramref name="projectFile"/>, if any.</summary>
    public static TedideProject? Find(IReadOnlyList<TedideProject> solution, string projectFile) =>
        solution.FirstOrDefault(p => p.FilePath is { } path
            && string.Equals(Path.GetFullPath(path), Path.GetFullPath(projectFile), StringComparison.OrdinalIgnoreCase));

    private static void Visit(IReadOnlyList<TedideProject> solution, TedideProject project, Dictionary<TedideProject, bool> state,
        List<TedideProject> order, List<TedideProject> path)
    {
        if (state.TryGetValue(project, out var done))
        {
            if (!done)
            {
                var cycle = path.SkipWhile(p => p != project).Append(project).Select(p => p.Name);
                throw new InvalidDataException($"Project references form a cycle: {string.Join(" -> ", cycle)}.");
            }
            return;
        }

        state[project] = false;
        path.Add(project);
        foreach (var referenced in DirectReferences(solution, project))
            Visit(solution, referenced, state, order, path);
        path.RemoveAt(path.Count - 1);
        state[project] = true;
        order.Add(project);
    }

    private static TedideProject Resolve(IReadOnlyList<TedideProject> solution, TedideProject project, string path)
    {
        var referenced = Find(solution, path)
            ?? throw new InvalidDataException($"{project.Name} references {Path.GetFileName(path)}, which isn't in the solution.");
        if (!referenced.IsLibrary)
            throw new InvalidDataException($"{project.Name} references {referenced.Name}, which isn't a library project.");
        return referenced;
    }
}
