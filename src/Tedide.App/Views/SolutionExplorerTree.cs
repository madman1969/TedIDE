using Tedide.Core;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// A TreeView showing the loaded solution's projects and their source/header files, recursing
/// into subdirectories (e.g. a project's src/ and include/ folders) to build a proper folder tree.
/// Raises <see cref="FileActivated"/> when the user activates (Enter/double-click) a file node.
/// </summary>
public sealed class SolutionExplorerTree : TreeView
{
    private static readonly string[] DisplayedExtensions = [".c", ".h", ".s", ".asm", ".inc"];
    private static readonly string[] IgnoredDirectoryNames = ["bin", "obj", ".git", ".vs"];

    public event Action<string>? FileActivated;

    public SolutionExplorerTree()
    {
        Accepted += (_, _) =>
        {
            if (SelectedObject is TreeNode { Tag: string path } && File.Exists(path))
                FileActivated?.Invoke(path);
        };
    }

    public void Rebuild(Workspace workspace)
    {
        ClearObjects();

        foreach (var project in workspace.Projects)
        {
            var projectNode = new TreeNode { Text = $"{project.Name} ({project.Target.ToCl65Id()})", Tag = project };

            // Show every source/header file in the project directory tree, not just the ones
            // passed to cl65 (SourceFiles) - headers are included via #include, never compiled
            // directly, but a real IDE still shows them for browsing/editing.
            if (Directory.Exists(project.Directory))
                AddDirectoryContents(projectNode, project.Directory);

            AddObject(projectNode);
        }

        ExpandAll();
    }

    private static void AddDirectoryContents(TreeNode parent, string directory)
    {
        var subdirectories = Directory.EnumerateDirectories(directory)
            .Where(d => !IgnoredDirectoryNames.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        foreach (var subdirectory in subdirectories)
        {
            var subdirectoryNode = new TreeNode { Text = Path.GetFileName(subdirectory), Tag = subdirectory };
            AddDirectoryContents(subdirectoryNode, subdirectory);

            // Skip folders that (recursively) contain nothing we'd display, e.g. an empty
            // build-output directory that slipped past the ignore list above.
            if (subdirectoryNode.Children.Count > 0)
                parent.Children.Add(subdirectoryNode);
        }

        var files = Directory.EnumerateFiles(directory)
            .Where(f => DisplayedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
            parent.Children.Add(new TreeNode { Text = Path.GetFileName(file), Tag = file });
    }
}
