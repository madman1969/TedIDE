using Tedide.Core;

namespace Tedide.App;

/// <summary>
/// The on-disk half of renaming a project (Project Settings' Name field): moves its folder, .tproj
/// and name-based sidecar files, and repoints the solution that references it. Kept out of
/// AppShell so it can be tested against real temp folders; AppShell does the UI half (unsaved-
/// changes prompt, reopening the editor's file, the Recent list).
///
/// The bug this replaces (confirmed live): for the usual layout - Foo/Foo.tsln beside Foo/Foo.tproj,
/// which every sample and every New Project uses - renaming moved the folder with the .tsln inside
/// it, then saved the solution to its old, now nonexistent path, crashing Tedide and leaving a
/// .tsln that pointed at a .tproj name that no longer existed (so the project couldn't be reopened
/// either), with its breakpoints/session files orphaned under the old name.
/// </summary>
internal static class ProjectRename
{
    /// <summary>Sidecar files named "{Project.Name}{suffix}" in the project folder - see
    /// TedideProject's Resolved*File properties. Renamed with the project so they stay attached.</summary>
    private static readonly string[] SidecarSuffixes = [".breakpoints.json", ".session.json", ".dbg", ".lbl"];

    /// <param name="NewSolutionFile">Where the solution file ended up, or null if there's no solution.</param>
    /// <param name="FolderNotRenamedReason">Why the folder wasn't renamed when it would have been,
    /// for telling the user - null if it was renamed, or never should have been.</param>
    internal sealed record Result(string NewProjectFile, string? NewSolutionFile, string? FolderNotRenamedReason);

    /// <summary>
    /// Applies a rename from <paramref name="oldName"/> to <paramref name="project"/>'s current
    /// Name, which has already been saved into the .tproj at <paramref name="oldProjectFile"/>.
    /// The folder is renamed only when it's named after the project (the New Project convention) -
    /// a project kept in some other folder, e.g. "MyStuff", doesn't get that folder renamed out from
    /// under it - and only when <paramref name="renameFolder"/> allows it and the new name is free.
    /// Updates <paramref name="project"/>'s FilePath and <paramref name="solution"/> (FilePath,
    /// ProjectPaths, Name) to match, and saves the solution. Throws on a file-system error part-way;
    /// the caller reports it.
    /// </summary>
    internal static Result Apply(TedideProject project, TedideSolution? solution, string oldName, string oldProjectFile, bool renameFolder)
    {
        var newName = project.Name;
        var oldDirectory = Path.GetDirectoryName(Path.GetFullPath(oldProjectFile))!;
        var oldSolutionFile = solution?.FilePath is { } s ? Path.GetFullPath(s) : null;

        // 1. The folder, when it's the project's own.
        var projectDirectory = oldDirectory;
        string? folderNotRenamedReason = null;
        if (string.Equals(Path.GetFileName(oldDirectory), oldName, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(oldDirectory) is { } parent)
        {
            var newDirectory = Path.Combine(parent, newName);
            if (!renameFolder)
                folderNotRenamedReason = "the open file has unsaved changes";
            else if (Directory.Exists(newDirectory) && !IsSamePathIgnoringCase(newDirectory, oldDirectory))
                folderNotRenamedReason = $"'{newDirectory}' already exists";
            else
            {
                try
                {
                    Directory.Move(oldDirectory, newDirectory);
                    projectDirectory = newDirectory;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // E.g. a file inside is open in another program (VICE, an editor) - rename the
                    // files in place instead of failing the whole rename.
                    folderNotRenamedReason = ex.Message;
                }
            }
        }

        // 2. The .tproj and sidecar files, inside wherever the project now lives.
        var movedProjectFile = Path.Combine(projectDirectory, Path.GetFileName(oldProjectFile));
        project.FilePath = RenameFile(movedProjectFile, Path.Combine(projectDirectory, newName + TedideProject.FileExtension));
        foreach (var suffix in SidecarSuffixes)
        {
            var sidecar = Path.Combine(projectDirectory, oldName + suffix);
            if (File.Exists(sidecar))
                RenameFile(sidecar, Path.Combine(projectDirectory, newName + suffix));
        }

        // 3. The solution: follow the folder move if it was inside it, take the new name if it's the
        // single-project solution named after this project, and repoint its ProjectPaths entry.
        if (solution is null || oldSolutionFile is null)
            return new Result(project.FilePath, null, folderNotRenamedReason);

        var solutionFile = oldSolutionFile;
        if (projectDirectory != oldDirectory && IsInside(oldSolutionFile, oldDirectory))
            solutionFile = Path.Combine(projectDirectory, Path.GetRelativePath(oldDirectory, oldSolutionFile));

        var isThisProjectsOwnSolution = solution.ProjectPaths.Count == 1
            && string.Equals(Path.GetFileNameWithoutExtension(solutionFile), oldName, StringComparison.OrdinalIgnoreCase);
        if (isThisProjectsOwnSolution)
        {
            solutionFile = RenameFile(solutionFile, Path.Combine(Path.GetDirectoryName(solutionFile)!, newName + TedideSolution.FileExtension));
            if (string.Equals(solution.Name, oldName, StringComparison.Ordinal))
                solution.Name = newName;
        }

        var oldSolutionDirectory = Path.GetDirectoryName(oldSolutionFile)!;
        var index = solution.ProjectPaths.FindIndex(p =>
            IsSamePathIgnoringCase(Path.GetFullPath(Path.Combine(oldSolutionDirectory, p)), Path.GetFullPath(oldProjectFile)));
        if (index >= 0)
            solution.ProjectPaths[index] = Path.GetRelativePath(Path.GetDirectoryName(solutionFile)!, project.FilePath).Replace('\\', '/');

        solution.Save(solutionFile);
        return new Result(project.FilePath, solution.FilePath, folderNotRenamedReason);
    }

    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/> unless that name is already
    /// taken by a different file (in which case it stays put) - returns wherever it ended up. A
    /// case-only change ("foo" -> "Foo") still goes through: on Windows both names are the same file.</summary>
    private static string RenameFile(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
            return from;
        if (File.Exists(to) && !IsSamePathIgnoringCase(from, to))
            return from;

        File.Move(from, to);
        return to;
    }

    private static bool IsInside(string path, string directory) =>
        path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsSamePathIgnoringCase(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
