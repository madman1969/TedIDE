using System.Drawing;
using Tedide.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.App.Views;

/// <summary>
/// A TreeView showing the loaded solution's projects and their source/header files, recursing
/// into subdirectories (e.g. a project's src/ and include/ folders) to build a proper folder tree.
/// Raises <see cref="FileActivated"/> when the user activates (Enter/double-click) a file node.
///
/// Adding and deleting files is driven entirely from a right-click (or Shift+F10) context menu:
/// "New File..." on a project or folder node creates a file there; a file node additionally offers
/// "Delete File". <see cref="NewFileRequested"/> and <see cref="DeleteFileRequested"/> carry those
/// requests up to the host, which owns the actual filesystem/project-file changes.
/// </summary>
public sealed class SolutionExplorerTree : TreeView
{
    /// <summary>Source/header extensions shown in the tree.</summary>
    public static readonly string[] DisplayedExtensions = [".c", ".h", ".s", ".asm", ".inc"];

    /// <summary>The subset of <see cref="DisplayedExtensions"/> that cl65 actually compiles, and
    /// so should be tracked in a project's SourceFiles - as opposed to headers, which are only
    /// ever pulled in via #include.</summary>
    public static readonly string[] CompilableExtensions = [".c", ".s", ".asm"];

    private static readonly string[] IgnoredDirectoryNames = ["bin", "obj", ".git", ".vs"];

    private readonly PopoverMenu _contextMenu;

    /// <summary>Raised with the target directory when "New File..." is chosen from the context menu.</summary>
    public event Action<string>? NewFileRequested;

    /// <summary>Raised with the file path when "Delete File" is chosen from the context menu.</summary>
    public event Action<string>? DeleteFileRequested;

    public event Action<string>? FileActivated;

    public SolutionExplorerTree()
    {
        _contextMenu = new PopoverMenu { Target = new WeakReference<View>(this) };

        Accepted += (_, _) =>
        {
            if (SelectedObject is TreeNode { Tag: string path } && File.Exists(path))
                FileActivated?.Invoke(path);
        };

        MouseEvent += (_, mouse) =>
        {
            if (!mouse.Flags.HasFlag(MouseFlags.RightButtonClicked))
                return;

            if (mouse.Position is not { } position || GetObjectOnRow(position.Y) is not TreeNode node)
                return;

            SelectedObject = node;
            if (TryShowContextMenu(node, mouse.ScreenPosition))
                mouse.Handled = true;
        };

        KeyDown += (_, key) =>
        {
            if (key == Key.F10.WithShift && SelectedObject is TreeNode node && GetObjectRow(node) is { } row)
                key.Handled = TryShowContextMenu(node, ViewportToScreen(new Point(0, row)));
        };
    }

    /// <summary>
    /// Builds and shows the context menu for the given node, if it has any applicable commands:
    /// "New File..." for a project or folder node, or "New File..."/"Delete File" for a file node.
    /// Returns false (showing nothing) for a node type with no applicable commands.
    /// </summary>
    private bool TryShowContextMenu(TreeNode node, Point screenPosition)
    {
        List<MenuItem> items = [];

        switch (node.Tag)
        {
            case TedideProject project:
                items.Add(new MenuItem("New File...", "", () => NewFileRequested?.Invoke(project.Directory)));
                break;
            case string path when Directory.Exists(path):
                items.Add(new MenuItem("New File...", "", () => NewFileRequested?.Invoke(path)));
                break;
            case string path when File.Exists(path):
                items.Add(new MenuItem("New File...", "", () => NewFileRequested?.Invoke(Path.GetDirectoryName(path)!)));
                items.Add(new MenuItem("Delete File", "", () => DeleteFileRequested?.Invoke(path)));
                break;
        }

        if (items.Count == 0)
            return false;

        _contextMenu.Root = new Menu(items);
        _contextMenu.MakeVisible(screenPosition);
        return true;
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

    /// <summary>
    /// Recursively enumerates every displayed source/header/assembly file under <paramref name="directory"/>,
    /// applying the same extension filter and ignored-directory list (bin/obj/.git/.vs) as the tree
    /// itself. Used by callers that need a flat file list rather than the tree structure - e.g.
    /// Find in Files.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string directory)
    {
        var subdirectories = Directory.EnumerateDirectories(directory)
            .Where(d => !IgnoredDirectoryNames.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase));

        foreach (var subdirectory in subdirectories)
            foreach (var file in EnumerateFiles(subdirectory))
                yield return file;

        var files = Directory.EnumerateFiles(directory)
            .Where(f => DisplayedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

        foreach (var file in files)
            yield return file;
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
