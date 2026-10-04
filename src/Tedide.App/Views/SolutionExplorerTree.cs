using System.Drawing;
using Tedide.Core;
using Tedide.Git;
using Terminal.Gui.Configuration;
using Color = Terminal.Gui.Drawing.Color;
using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Scheme = Terminal.Gui.Drawing.Scheme;

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
///
/// A project's folders outside its own (see <see cref="TedideProject.LinkedDirectories"/>: shared
/// sources, or an include path elsewhere) are listed under it too, named by their relative path
/// ("..\src"), so every file it builds can be found here.
///
/// A loaded solution is the root node, offering Add New/Existing Project and Build/Clean Solution.
/// Each project node offers Set as Startup Project, Build, Clean, Settings, Remove from Solution
/// and Delete Project; the startup project is drawn in bold, as in Visual Studio.
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
    private bool _contextMenuOpen;

    /// <summary>Raised with the target directory, and the project whose node it was listed under,
    /// when "New File..." is chosen from the context menu.</summary>
    public event Action<string, TedideProject?>? NewFileRequested;

    /// <summary>Raised with the target directory, and the project whose node it was listed under,
    /// when "Add Existing Item..." is chosen from the context menu.</summary>
    public event Action<string, TedideProject?>? AddExistingItemRequested;

    /// <summary>The project each folder and file node was listed under - for a folder several
    /// projects share, which one a new file joins.</summary>
    private readonly Dictionary<ITreeNode, TedideProject> _owners = [];

    /// <summary>Raised with the file path when "Rename File" is chosen from the context menu.</summary>
    public event Action<string>? RenameFileRequested;

    /// <summary>Raised with the file path when "Delete File" is chosen from the context menu.</summary>
    public event Action<string>? DeleteFileRequested;

    public event Action<string>? FileActivated;

    public event Action? AddNewProjectRequested;
    public event Action? AddExistingProjectRequested;
    public event Action? BuildSolutionRequested;
    public event Action? CleanSolutionRequested;
    public event Action<TedideProject>? SetStartupProjectRequested;
    public event Action<TedideProject>? BuildProjectRequested;
    public event Action<TedideProject>? CleanProjectRequested;
    public event Action<TedideProject>? ProjectSettingsRequested;
    public event Action<TedideProject>? RemoveProjectRequested;
    public event Action<TedideProject>? DeleteProjectRequested;

    /// <summary>The startup project, drawn in bold - see <see cref="Rebuild"/>.</summary>
    private TedideProject? _startupProject;

    /// <summary>Changed files by full path, from git - see <see cref="SetGitStatus"/>.</summary>
    private IReadOnlyDictionary<string, GitFileStatus> _gitFiles = new Dictionary<string, GitFileStatus>();

    /// <summary>Raised at the end of every <see cref="Rebuild"/> - the files shown may have changed.</summary>
    public event Action? Rebuilt;

    public SolutionExplorerTree()
    {
        _contextMenu = new PopoverMenu { Target = new WeakReference<View>(this) };

        // Folder nodes (src, include, Generated Files...) stand out from the files in them. Read
        // from the tree's own scheme at draw time, so a theme switch restyles them immediately.
        // Every other node gets that scheme explicitly: returning null (documented as "use the
        // default") drew files white-on-black whatever the theme.
        ColorGetter = node => node switch
        {
            FolderNode => FolderScheme(GetScheme()),
            TreeNode { Tag: TedideProject project } when project == _startupProject => StartupScheme(GetScheme()),
            TreeNode { Tag: string path } when _gitFiles.TryGetValue(path, out var status) => GitScheme(GetScheme(), status.Marker),
            _ => GetScheme(),
        };

        // Choosing a context-menu item also reaches the tree as an activation, which toggles the
        // selected node - Set as Startup Project collapsed the whole solution (confirmed live). So
        // while the menu is up, and until the event that closed it has finished, activation is
        // ignored.
        _contextMenu.VisibleChanged += (_, _) =>
        {
            if (_contextMenu.Visible)
                _contextMenuOpen = true;
            else
                Application.AddTimeout(TimeSpan.Zero, () =>
                {
                    _contextMenuOpen = false;
                    return false;
                });
        };
        Activating += (_, e) =>
        {
            if (_contextMenuOpen)
                e.Handled = true;
        };

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
        List<View> items = [];

        var owner = _owners.GetValueOrDefault(node);
        switch (node.Tag)
        {
            case TedideSolution:
                items.Add(Item("Add New Project...", () => AddNewProjectRequested?.Invoke()));
                items.Add(Item("Add Existing Project...", () => AddExistingProjectRequested?.Invoke()));
                items.Add(new Line());
                items.Add(Item("Build Solution", () => BuildSolutionRequested?.Invoke()));
                items.Add(Item("Clean Solution", () => CleanSolutionRequested?.Invoke()));
                break;
            case TedideProject project:
                if (project != _startupProject)
                    items.Add(Item("Set as Startup Project", () => SetStartupProjectRequested?.Invoke(project)));
                items.Add(Item("Build", () => BuildProjectRequested?.Invoke(project)));
                items.Add(Item("Clean", () => CleanProjectRequested?.Invoke(project)));
                items.Add(Item("Settings...", () => ProjectSettingsRequested?.Invoke(project)));
                if (_hasSolution)
                {
                    items.Add(new Line());
                    items.Add(Item("Remove from Solution", () => RemoveProjectRequested?.Invoke(project)));
                    items.Add(Item("Delete Project...", () => DeleteProjectRequested?.Invoke(project)));
                }
                break;
            case string path when Directory.Exists(path):
                items.Add(Item("New File...", () => NewFileRequested?.Invoke(path, owner)));
                items.Add(Item("Add Existing Item...", () => AddExistingItemRequested?.Invoke(path, owner)));
                break;
            case string path when File.Exists(path):
                items.Add(Item("New File...", () => NewFileRequested?.Invoke(Path.GetDirectoryName(path)!, owner)));
                items.Add(Item("Add Existing Item...", () => AddExistingItemRequested?.Invoke(Path.GetDirectoryName(path)!, owner)));
                items.Add(Item("Rename File", () => RenameFileRequested?.Invoke(path)));
                items.Add(Item("Delete File", () => DeleteFileRequested?.Invoke(path)));
                break;
        }

        if (items.Count == 0)
            return false;

        _contextMenu.Root = new Menu(items);
        _contextMenu.MakeVisible(screenPosition);
        return true;
    }

    /// <summary>
    /// A context-menu item whose action runs on the next main-loop pass, once the menu has finished
    /// handling the choice - including passing it to the tree as an activation, which the
    /// <see cref="_contextMenuOpen"/> guard then still catches. Run straight away, an action that
    /// opened a dialog (Delete Project's confirmation) let the guard lapse inside the dialog's own
    /// loop, and the tree collapsed afterwards (confirmed live).
    /// </summary>
    private static MenuItem Item(string text, Action action) =>
        new(text, "", () => Application.AddTimeout(TimeSpan.Zero, () =>
        {
            action();
            return false;
        }));

    /// <summary>A folder, or the "Generated Files" group - drawn with <see cref="FolderScheme"/>.</summary>
    internal sealed class FolderNode : TreeNode;

    /// <summary>
    /// <paramref name="treeScheme"/> with unselected text in the theme's type color - see
    /// <see cref="TreeNodeSchemes.Emphasised"/>, which the Doc Viewer's book titles share.
    /// </summary>
    internal static Scheme FolderScheme(Scheme treeScheme) => TreeNodeSchemes.Emphasised(treeScheme);

    /// <summary>What the startup project's name starts with: what Run starts. The surest of its
    /// three marks - bold doesn't show in every theme and font, and colour alone is easy to miss.</summary>
    internal const string StartupMarker = "▶ ";

    /// <summary>
    /// The startup project: bold, selected or not, and when not selected in a colour of the
    /// theme's own that differs from both the files' and the folders' (see <see cref="StartupColor"/>)
    /// - or, in a theme with no such colour, underlined.
    /// </summary>
    internal static Scheme StartupScheme(Scheme treeScheme)
    {
        var (color, distinct) = StartupColor(treeScheme);
        var style = treeScheme.Normal.Style | Terminal.Gui.Drawing.TextStyle.Bold
            | (distinct ? Terminal.Gui.Drawing.TextStyle.None : Terminal.Gui.Drawing.TextStyle.Underline);
        return new Scheme(treeScheme)
        {
            Normal = new Terminal.Gui.Drawing.Attribute(color, treeScheme.Normal.Background, style),
            Focus = treeScheme.Focus with { Style = treeScheme.Focus.Style | Terminal.Gui.Drawing.TextStyle.Bold },
            Active = treeScheme.Active with { Style = treeScheme.Active.Style | Terminal.Gui.Drawing.TextStyle.Bold },
        };
    }

    /// <summary>
    /// The first of the theme's keyword, function, string, number and constant colours that, made
    /// readable on its background, still differs from file and folder text - the keyword colour in
    /// most themes. A single-hue theme like Amber Phosphor has none (Distinct false): its keyword
    /// colour is used anyway.
    /// </summary>
    private static (Color Color, bool Distinct) StartupColor(Scheme treeScheme)
    {
        var background = treeScheme.Normal.Background;
        var folder = FolderScheme(treeScheme).Normal.Foreground;
        Color[] candidates =
        [
            treeScheme.CodeKeyword.Foreground, treeScheme.CodeFunctionName.Foreground, treeScheme.CodeString.Foreground,
            treeScheme.CodeNumber.Foreground, treeScheme.CodeConstant.Foreground,
        ];
        var readable = candidates.Select(c => ThemeSwitcher.Readable(c, background)).ToList();
        foreach (var color in readable)
            if (!ThemeSwitcher.Indistinguishable(color, treeScheme.Normal.Foreground) && !ThemeSwitcher.Indistinguishable(color, folder))
                return (color, true);
        return (readable[0], false);
    }

    private bool _hasSolution;

    /// <summary>
    /// <paramref name="treeScheme"/> with unselected text in the colour for a git status letter -
    /// VS Code's: green for a new file, amber for a changed one, red for a conflict. Fixed hues
    /// rather than theme roles (no role is reliably green - a string is red-brown in VS2026 Dark,
    /// which made a new file look like an error), each made readable on the theme's background.
    /// </summary>
    internal static Scheme GitScheme(Scheme treeScheme, char marker)
    {
        var hue = marker switch
        {
            '!' => new Color(0xE4, 0x67, 0x6B),
            '?' or 'A' => new Color(0x73, 0xC9, 0x91),
            _ => new Color(0xE2, 0xC0, 0x8D),
        };
        var background = treeScheme.Normal.Background;
        return new Scheme(treeScheme)
        {
            Normal = new Terminal.Gui.Drawing.Attribute(ThemeSwitcher.Readable(hue, background), background, treeScheme.Normal.Style),
        };
    }

    /// <summary>
    /// Shows git's status letter after each changed file's name (main.c M, notes.txt ?) and colours
    /// it - see <see cref="GitFileStatus.Marker"/>. Updates the nodes in place, so the tree keeps its
    /// expansion and selection; <see cref="Rebuild"/> applies the latest status to new nodes.
    /// </summary>
    public void SetGitStatus(IReadOnlyDictionary<string, GitFileStatus> files)
    {
        _gitFiles = files;
        ApplyGitMarkers();
        SetNeedsDraw();
    }

    private void ApplyGitMarkers()
    {
        foreach (var root in Objects ?? [])
            ApplyGitMarkers(root);
    }

    private void ApplyGitMarkers(ITreeNode node)
    {
        if (node is TreeNode { Tag: string path } file && node is not FolderNode)
            file.Text = Path.GetFileName(path) + (_gitFiles.TryGetValue(path, out var status) ? $" {status.Marker}" : "");
        foreach (var child in node.Children)
            ApplyGitMarkers(child);
    }

    public void Rebuild(Workspace workspace)
    {
        ClearObjects();
        _owners.Clear();
        _startupProject = workspace.ActiveProject;
        _hasSolution = workspace.Solution is not null;

        // A solution gets a root node of its own, holding its projects; a bare .tproj is the root itself.
        TreeNode? solutionNode = null;
        if (workspace.Solution is { } solution)
        {
            var count = workspace.Projects.Count;
            solutionNode = new TreeNode { Text = $"Solution '{solution.Name}' ({count} project{(count == 1 ? "" : "s")})", Tag = solution };
        }

        foreach (var project in workspace.Projects)
        {
            var kind = project.IsLibrary ? ", library" : "";
            var marker = project == _startupProject ? StartupMarker : "";
            var projectNode = new TreeNode { Text = $"{marker}{project.Name} ({project.Target.ToCl65Id()}{kind})", Tag = project };

            // Show every source/header file in the project directory tree, not just the ones
            // passed to cl65 (SourceFiles) - headers are included via #include, never compiled
            // directly, but a real IDE still shows them for browsing/editing.
            if (Directory.Exists(project.Directory))
                AddDirectoryContents(projectNode, project.Directory);

            // Folders it uses from outside its own, after its own files.
            foreach (var linked in project.LinkedDirectories.Where(Directory.Exists).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var linkedNode = new FolderNode { Text = Path.GetRelativePath(project.Directory, linked), Tag = linked };
                AddDirectoryContents(linkedNode, linked);
                if (linkedNode.Children.Count > 0)
                    projectNode.Children.Add(linkedNode);
            }

            foreach (var child in projectNode.Children)
                RecordOwner(child, project);

            AddGeneratedFilesNode(projectNode, project);

            if (solutionNode is not null)
                solutionNode.Children.Add(projectNode);
            else
                AddObject(projectNode);
        }

        // Added once complete: the tree reads a node's children when it's added, so projects put
        // under it afterwards never showed.
        if (solutionNode is not null)
            AddObject(solutionNode);
        ApplyGitMarkers();

        ExpandAll();
        Rebuilt?.Invoke();
    }

    private void RecordOwner(ITreeNode node, TedideProject project)
    {
        _owners[node] = project;
        foreach (var child in node.Children)
            RecordOwner(child, project);
    }

    /// <summary>
    /// Every file the tree lists for <paramref name="project"/>: those in its own folder and in its
    /// <see cref="TedideProject.LinkedDirectories"/>. Find in Files, code completion and Go To
    /// Definition search these.
    /// </summary>
    public static IEnumerable<string> EnumerateProjectFiles(TedideProject project) =>
        new[] { project.Directory }.Concat(project.LinkedDirectories)
            .Where(Directory.Exists)
            .SelectMany(EnumerateFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Recursively enumerates every displayed source/header/assembly file under <paramref name="directory"/>,
    /// applying the same extension filter and ignored-directory list (bin/obj/.git/.vs) as the tree
    /// itself. Used by callers that need a flat file list rather than the tree structure - e.g.
    /// Find in Files.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string directory)
    {
        var subdirectories = Directory.EnumerateDirectories(directory).Where(IsBrowsableSubdirectory);

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

        var generatedNode = new FolderNode { Text = "Generated Files" };
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

    /// <summary>
    /// Whether a project's subfolder is shown as part of it: not bin/obj/.git/.vs, and not a folder
    /// holding a .tproj of its own - that's another project, shown (if it's in the solution) as its
    /// own node rather than as a folder of this one. A project created inside another's folder
    /// appeared twice, once nested under it.
    /// </summary>
    private static bool IsBrowsableSubdirectory(string directory) =>
        !IgnoredDirectoryNames.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase)
        && !Directory.EnumerateFiles(directory, "*" + TedideProject.FileExtension).Any();

    private static void AddDirectoryContents(TreeNode parent, string directory)
    {
        var subdirectories = Directory.EnumerateDirectories(directory)
            .Where(IsBrowsableSubdirectory)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        foreach (var subdirectory in subdirectories)
        {
            var subdirectoryNode = new FolderNode { Text = Path.GetFileName(subdirectory), Tag = subdirectory };
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
