using System.Drawing;
using Tedide.Core.Navigation;
using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Scheme = Terminal.Gui.Drawing.Scheme;

namespace Tedide.App.Views;

/// <summary>
/// View > Document Outline, as in Visual Studio: the shown file's structure (see
/// <see cref="DocumentOutline"/>) as a tree, with a filter box above it. Each node starts with a mark
/// for its kind and is coloured like that kind in the editor. Enter or a double-click goes to it; the
/// selection follows the caret; the right-click menu adds Find All References, Rename Symbol,
/// sorting by name, and Collapse/Expand All. Collapsed nodes stay collapsed as the file is edited.
/// </summary>
public sealed class DocumentOutlineView : View
{
    private readonly TextField _filterField;
    private readonly TreeView _tree;
    private readonly Label _emptyLabel;
    private readonly PopoverMenu _contextMenu;
    private bool _contextMenuOpen;

    private IReadOnlyList<OutlineNode> _outline = [];
    private bool _sortByName;

    /// <summary>The nodes the user collapsed, by <see cref="KeyOf"/> - kept across rebuilds, which
    /// happen after every pause in typing.</summary>
    private readonly HashSet<string> _collapsed = [];

    /// <summary>A node was chosen (Enter, a double-click, or Go To).</summary>
    public event Action<OutlineNode>? NodeActivated;

    public event Action<OutlineNode>? FindReferencesRequested;
    public event Action<OutlineNode>? RenameRequested;

    public DocumentOutlineView()
    {
        CanFocus = true;
        _filterField = new TextField { X = 0, Y = 0, Width = Dim.Fill() };
        _filterField.TextChanged += (_, _) => Rebuild();

        _tree = new TreeView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        _tree.ColorGetter = node => node is TreeNode { Tag: OutlineNode outline } ? SchemeFor(outline, _tree.GetScheme()) : _tree.GetScheme();
        _tree.Accepted += (_, _) =>
        {
            if (!_contextMenuOpen && _tree.SelectedObject is TreeNode { Tag: OutlineNode node })
                NodeActivated?.Invoke(node);
        };

        _emptyLabel = new Label { X = 0, Y = 1, Width = Dim.Fill(), Text = "No outline for this file.", Visible = false };

        // As in the Solution Explorer: choosing a menu item also reaches the tree as an activation,
        // which would toggle the node - so activation is ignored while the menu is up.
        _contextMenu = new PopoverMenu { Target = new WeakReference<View>(_tree) };
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
        _tree.Activating += (_, e) =>
        {
            if (_contextMenuOpen)
                e.Handled = true;
        };
        _tree.MouseEvent += (_, mouse) =>
        {
            if (!mouse.Flags.HasFlag(MouseFlags.RightButtonClicked) || mouse.Position is not { } position)
                return;
            if (_tree.GetObjectOnRow(position.Y) is TreeNode node)
                _tree.SelectedObject = node;
            ShowContextMenu(mouse.ScreenPosition);
            mouse.Handled = true;
        };
        _tree.KeyDown += (_, key) =>
        {
            if (key == Key.F10.WithShift)
            {
                var row = _tree.SelectedObject is { } selected ? _tree.GetObjectRow(selected) ?? 0 : 0;
                ShowContextMenu(_tree.ViewportToScreen(new Point(0, row)));
                key.Handled = true;
            }
        };
        // Collapsing and expanding by hand is remembered for the next rebuild.
        _tree.ObjectCollapsed(node => _collapsed.Add(KeyOf(node)), node => _collapsed.Remove(KeyOf(node)));

        Add(_filterField, _tree, _emptyLabel);
    }

    /// <summary>The outline shown - what <see cref="Show"/> was last given, before filtering and sorting.</summary>
    internal IReadOnlyList<OutlineNode> Outline => _outline;

    internal TreeView Tree => _tree;
    internal TextField FilterField => _filterField;

    internal bool SortByName
    {
        get => _sortByName;
        set
        {
            _sortByName = value;
            Rebuild();
        }
    }

    /// <summary>Shows a file's outline - null for a file it doesn't apply to, or no file.</summary>
    public void Show(IReadOnlyList<OutlineNode>? outline)
    {
        var fileChanged = outline is null || _outline.Count == 0 || outline.Count == 0
            || _outline[0].Definition.FilePath != outline[0].Definition.FilePath;
        if (fileChanged)
            _collapsed.Clear();
        _outline = outline ?? [];
        _emptyLabel.Visible = outline is null || outline.Count == 0;
        _emptyLabel.Text = outline is null ? "No outline for this file." : "Nothing to outline yet.";
        Rebuild();
    }

    private void Rebuild()
    {
        var selectedKey = _tree.SelectedObject is { } selected ? KeyOf(selected) : null;
        var scroll = _tree.ScrollOffsetVertical;

        var nodes = DocumentOutline.Filter(_outline, _filterField.Text);
        if (_sortByName)
            nodes = DocumentOutline.SortedByName(nodes);

        _tree.ClearObjects();
        var built = nodes.Select(n => Build(n, "")).ToList();
        _tree.AddObjects(built);
        _tree.ExpandAll();
        // A filter shows every match; otherwise what was collapsed stays collapsed.
        if (string.IsNullOrWhiteSpace(_filterField.Text))
            foreach (var node in Flatten(built).Where(n => _collapsed.Contains(KeyOf(n))))
                _tree.Collapse(node);

        if (selectedKey is not null && Flatten(built).FirstOrDefault(n => KeyOf(n) == selectedKey) is { } again)
            _tree.SelectedObject = again;
        _tree.ScrollOffsetVertical = scroll;
        _tree.SetNeedsDraw();
    }

    /// <summary>A node, with its children added before the tree sees it (see the Solution Explorer's
    /// note on AddObject). Its key - the names leading to it - is kept in the tag's wrapper.</summary>
    private static TreeNode Build(OutlineNode node, string parentKey)
    {
        var key = $"{parentKey}/{node.Kind}:{node.Definition.Name}";
        var treeNode = new KeyedNode(key) { Text = $"{Mark(node.Kind)} {node.Text}", Tag = node };
        foreach (var child in node.Children)
            treeNode.Children.Add(Build(child, key));
        return treeNode;
    }

    private sealed class KeyedNode(string key) : TreeNode
    {
        public string Key { get; } = key;
    }

    private static string KeyOf(ITreeNode node) => node is KeyedNode keyed ? keyed.Key : node.Text;

    private static IEnumerable<ITreeNode> Flatten(IEnumerable<ITreeNode> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    /// <summary>
    /// Selects the innermost node covering <paramref name="line"/> - or, if it's inside a collapsed
    /// node, that node - so the outline follows the caret. Leaves the selection alone where nothing
    /// covers the line.
    /// </summary>
    public void FollowCaret(int line)
    {
        TreeNode? target = null;
        var level = _tree.Objects?.ToList() ?? [];
        while (level.OfType<TreeNode>().FirstOrDefault(n => n.Tag is OutlineNode o && o.Covers(line)) is { } covering)
        {
            target = covering;
            if (!_tree.IsExpanded(covering))
                break;
            level = covering.Children.ToList();
        }
        if (target is null || ReferenceEquals(target, _tree.SelectedObject))
            return;
        _tree.SelectedObject = target;
        _tree.EnsureVisible(target);
        _tree.SetNeedsDraw();
    }

    public void CollapseAll()
    {
        foreach (var node in Flatten(_tree.Objects ?? []).Where(n => n.Children.Count > 0))
        {
            _collapsed.Add(KeyOf(node));
            _tree.Collapse(node);
        }
        _tree.SetNeedsDraw();
    }

    public void ExpandAll()
    {
        _collapsed.Clear();
        _tree.ExpandAll();
        _tree.SetNeedsDraw();
    }

    private void ShowContextMenu(Point screenPosition)
    {
        var node = _tree.SelectedObject is TreeNode { Tag: OutlineNode selected } ? selected : null;
        List<View> items = [];
        if (node is not null)
        {
            items.Add(Item("Go To", () => NodeActivated?.Invoke(node)));
            items.Add(Item("Find All References", () => FindReferencesRequested?.Invoke(node)));
            items.Add(Item("Rename Symbol...", () => RenameRequested?.Invoke(node)));
            items.Add(new Line());
        }
        items.Add(Item(_sortByName ? "Sort by Position" : "Sort by Name", () => SortByName = !_sortByName));
        items.Add(Item("Collapse All", CollapseAll));
        items.Add(Item("Expand All", ExpandAll));
        _contextMenu.Root = new Menu(items);
        _contextMenu.MakeVisible(screenPosition);
    }

    /// <summary>A menu item whose action runs once the menu has finished - see the Solution Explorer's.</summary>
    private static MenuItem Item(string text, Action action) =>
        new(text, "", () => Application.AddTimeout(TimeSpan.Zero, () =>
        {
            action();
            return false;
        }));

    /// <summary>The mark before each node's text: what kind of symbol it is, readable in any theme.</summary>
    internal static string Mark(SymbolKind kind) => kind switch
    {
        SymbolKind.Function or SymbolKind.Prototype => "ƒ",
        SymbolKind.Macro => "#",
        SymbolKind.Typedef or SymbolKind.Tag => "T",
        SymbolKind.Member or SymbolKind.EnumConstant => "·",
        SymbolKind.Constant => "=",
        SymbolKind.Label => "›",
        SymbolKind.Import => "←",
        _ => "•",
    };

    /// <summary>A node in its kind's editor colour - functions as functions, types as types, macros
    /// as keywords, constants as numbers - and an inactive one in the comment colour; selected rows
    /// keep the tree's own look.</summary>
    internal static Scheme SchemeFor(OutlineNode node, Scheme tree)
    {
        var role = node.Inactive ? tree.CodeComment
            : node.Kind switch
            {
                SymbolKind.Function or SymbolKind.Prototype or SymbolKind.Label => tree.CodeFunctionName,
                SymbolKind.Typedef or SymbolKind.Tag => tree.CodeType,
                SymbolKind.Macro => tree.CodeKeyword,
                SymbolKind.Constant or SymbolKind.EnumConstant => tree.CodeNumber,
                _ => tree.Normal,
            };
        var background = tree.Normal.Background;
        return new Scheme(tree) { Normal = new Attribute(ThemeSwitcher.Readable(role.Foreground, background), background, tree.Normal.Style) };
    }
}

/// <summary>Collapse and expand notifications for a TreeView - see <see cref="DocumentOutlineView"/>.</summary>
internal static class TreeViewCollapseExtensions
{
    public static void ObjectCollapsed(this TreeView tree, Action<ITreeNode> collapsed, Action<ITreeNode> expanded)
    {
        tree.KeyDown += (_, key) => Track(tree, collapsed, expanded);
        tree.MouseEvent += (_, _) => Track(tree, collapsed, expanded);
    }

    // Terminal.Gui raises no event when a node is collapsed or expanded, so the selected node's
    // state is checked after each key or click that could have changed it.
    private static void Track(TreeView tree, Action<ITreeNode> collapsed, Action<ITreeNode> expanded) =>
        Application.AddTimeout(TimeSpan.Zero, () =>
        {
            if (tree.SelectedObject is { } node && node.Children.Count > 0)
            {
                if (tree.IsExpanded(node))
                    expanded(node);
                else
                    collapsed(node);
            }
            return false;
        });
}
