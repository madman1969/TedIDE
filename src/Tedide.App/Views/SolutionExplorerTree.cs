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
/// Adding, renaming and deleting files is driven entirely from a right-click (or Shift+F10)
/// context menu: "New File..." on a project or folder node creates a file there, "Add Existing
/// Item..." (right below it) copies one or more files picked from anywhere on disk into that same
/// folder; a file node additionally offers "Rename File" and "Delete File". <see cref="NewFileRequested"/>,
/// <see cref="AddExistingItemRequested"/>, <see cref="RenameFileRequested"/> and
/// <see cref="DeleteFileRequested"/> carry those requests up to the host, which owns the actual
/// filesystem/project-file changes.
/// </summary>
public sealed class SolutionExplorerTree : TreeView
{
    /// <summary>Source/header extensions shown in the tree. ".cfg" (a ld65 linker config file) is
    /// included even though it's never compiled - see <see cref="CompilableExtensions"/> - so a
    /// project's linker config can be opened and edited from here like any other file.</summary>
    public static readonly string[] DisplayedExtensions = [".c", ".h", ".s", ".asm", ".inc", ".cfg"];

    /// <summary>The subset of <see cref="DisplayedExtensions"/> that cl65 actually compiles, and
    /// so should be tracked in a project's SourceFiles - as opposed to headers, which are only
    /// ever pulled in via #include.</summary>
    public static readonly string[] CompilableExtensions = [".c", ".s", ".asm"];

    private static readonly string[] IgnoredDirectoryNames = ["bin", "obj", ".git", ".vs"];

    private readonly PopoverMenu _contextMenu;

    /// <summary>Raised with the target directory when "New File..." is chosen from the context menu.</summary>
    public event Action<string>? NewFileRequested;

    /// <summary>Raised with the target directory when "Add Existing Item..." is chosen from the context menu.</summary>
    public event Action<string>? AddExistingItemRequested;

    /// <summary>Raised with the file path when "Rename File" is chosen from the context menu.</summary>
    public event Action<string>? RenameFileRequested;

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
    /// "New File..." for a folder node, or "New File..."/"Rename File"/"Delete File" for a file
    /// node. The project root node itself offers neither - New File is deliberately only
    /// available inside one of its subfolders (typically src/include), not directly in the
    /// project's own directory. Returns false (showing nothing) for a node type with no
    /// applicable commands.
    /// </summary>
    private bool TryShowContextMenu(TreeNode node, Point screenPosition)
    {
        List<MenuItem> items = [];

        switch (node.Tag)
        {
            case string path when Directory.Exists(path):
                items.Add(new MenuItem("New File...", "", () => NewFileRequested?.Invoke(path)));
                items.Add(new MenuItem("Add Existing Item...", "", () => AddExistingItemRequested?.Invoke(path)));
                break;
            case string path when File.Exists(path):
                items.Add(new MenuItem("New File...", "", () => NewFileRequested?.Invoke(Path.GetDirectoryName(path)!)));
                items.Add(new MenuItem("Add Existing Item...", "", () => AddExistingItemRequested?.Invoke(Path.GetDirectoryName(path)!)));
                items.Add(new MenuItem("Rename File", "", () => RenameFileRequested?.Invoke(path)));
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

            AddGeneratedFilesNode(projectNode, project);

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

    /// <summary>
    /// Adds a "Generated Files" node listing build-generated files that live outside the normal
    /// source tree: the cc65 assembler listings (.lst, one per source file) if
    /// <see cref="TedideProject.GenerateAssemblyListing"/> is on, the ld65 linker map (lnk.map) if
    /// <see cref="TedideProject.GenerateLinkerMap"/> is on, the ld65 label file ({Name}.lbl) if
    /// <see cref="TedideProject.ExportLabels"/> is on, and the debug info file ({Name}.dbg) if
    /// <see cref="TedideProject.GenerateDebugInfo"/> is on - each only once it's actually been written
    /// (a project, or a single source file in it, that's never been built won't have one yet).
    /// Omitted entirely when there's nothing to show, so a never-built (or nothing-enabled) project
    /// doesn't get an empty node. Has no Tag - it's not itself a file or directory, so the
    /// right-click context menu offers nothing for it.
    /// </summary>
    private static void AddGeneratedFilesNode(TreeNode projectNode, TedideProject project)
    {
        var generatedFiles = new List<string>();

        if (project.GenerateAssemblyListing)
            generatedFiles.AddRange(project.ResolvedListingFiles.Where(File.Exists));
        if (project.GenerateLinkerMap && File.Exists(project.ResolvedMapFile))
            generatedFiles.Add(project.ResolvedMapFile);
        if (project.ExportLabels && File.Exists(project.ResolvedLabelsFile))
            generatedFiles.Add(project.ResolvedLabelsFile);
        if (project.GenerateDebugInfo && File.Exists(project.ResolvedDebugInfoFile))
            generatedFiles.Add(project.ResolvedDebugInfoFile);

        if (generatedFiles.Count == 0)
            return;

        var generatedNode = new TreeNode { Text = "Generated Files" };
        foreach (var generatedFile in generatedFiles)
        {
            generatedNode.Children.Add(new TreeNode
            {
                Text = Path.GetFileName(generatedFile),
                Tag = generatedFile,
            });
        }
        projectNode.Children.Add(generatedNode);
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
