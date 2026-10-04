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
/// for its kind and is coloured like that kind in the editor. A click goes to it, keeping the focus
/// here to click on through; Enter or a double-click goes to it in the editor. The selection follows
/// the caret; the right-click menu adds Find All References, Rename Symbol, sorting by position, name
/// or kind, and Collapse/Expand All. Each file's collapsed nodes stay collapsed as it's edited and
/// when its tab is shown again. In the filter box, Esc clears it and Down or Enter moves into the tree.
/// </summary>
public sealed class DocumentOutlineView : View
{
    private readonly TextField _filterField;
    private readonly TreeView _tree;
    private readonly Label _emptyLabel;
    private readonly PopoverMenu _contextMenu;
    private bool _contextMenuOpen;

    private IReadOnlyList<OutlineNode> _outline = [];
    private OutlineSort _sort;
    private readonly Action<Action> _post;

    /// <summary>The file shown, whose collapsed nodes are <see cref="Collapsed"/>.</summary>
    private string? _path;

    /// <summary>Each file's collapsed nodes, by <see cref="KeyOf"/> - kept across rebuilds, which
    /// happen after every pause in typing, and tab switches.</summary>
    private readonly Dictionary<string, HashSet<string>> _collapsedByFile = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> Collapsed => _collapsedByFile.TryGetValue(_path ?? "", out var set) ? set : _collapsedByFile[_path ?? ""] = [];

    /// <summary>The last node gone to, and when - so a click Terminal.Gui also reports as an
    /// activation doesn't go there twice.</summary>
    private (OutlineNode Node, long At)? _lastActivation;

    /// <summary>A node was chosen: true with a single click, which leaves the focus in the outline;
    /// false with Enter, a double-click or Go To, which hand it to the editor.</summary>
    public event Action<OutlineNode, bool>? NodeActivated;

    public event Action<OutlineNode>? FindReferencesRequested;
    public event Action<OutlineNode>? RenameRequested;

    public DocumentOutlineView()
        : this(action => Application.AddTimeout(TimeSpan.Zero, () =>
        {
            action();
            return false;
        }))
    {
    }

    /// <param name="post">Runs an action once the current input event is finished - the main loop's
    /// next pass, or straight away in a test.</param>
    internal DocumentOutlineView(Action<Action> post)
    {
        _post = post;
        CanFocus = true;
        _filterField = new TextField { X = 0, Y = 0, Width = Dim.Fill() };
        _filterField.TextChanged += (_, _) => Rebuild();
        _filterField.KeyDown += (_, key) =>
        {
            if (key == Key.Esc && _filterField.Text.Length > 0)
            {
                _filterField.Text = "";
                key.Handled = true;
            }
            else if (key == Key.CursorDown || key == Key.Enter)
            {
                // Into the tree, on the first match - and Enter goes to it.
                if (_tree.Objects?.FirstOrDefault() is { } first)
                {
                    if (_tree.SelectedObject is null || key == Key.Enter)
                        _tree.SelectedObject = first;
                    _tree.SetFocus();
                    if (key == Key.Enter && _tree.SelectedObject is TreeNode { Tag: OutlineNode node })
                        Activate(node, keepFocus: false);
                }
                key.Handled = true;
            }
        };

        _tree = new TreeView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        _tree.ColorGetter = node => node is TreeNode { Tag: OutlineNode outline } ? SchemeFor(outline, _tree.GetScheme()) : _tree.GetScheme();
        _tree.Accepted += (_, _) =>
        {
            if (!_contextMenuOpen && _tree.SelectedObject is TreeNode { Tag: OutlineNode node })
                Activate(node, keepFocus: false);
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
            if (mouse.Position is not { } position)
                return;
            if (mouse.Flags.HasFlag(MouseFlags.RightButtonClicked))
            {
                if (_tree.GetObjectOnRow(position.Y) is TreeNode node)
                    _tree.SelectedObject = node;
                ShowContextMenu(mouse.ScreenPosition);
                mouse.Handled = true;
            }
            else if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked) && _tree.GetObjectOnRow(position.Y) is TreeNode { Tag: OutlineNode clicked } node)
            {
                // A click on the expand/collapse mark only toggles; anywhere else on the row goes
                // there, as in Visual Studio. Which it was shows once the tree has handled the click.
                var expanded = _tree.IsExpanded(node);
                _post(() =>
                {
                    if (!_contextMenuOpen && _tree.IsExpanded(node) == expanded)
                        Activate(clicked, keepFocus: true);
                });
            }
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
        _tree.ObjectCollapsed(node => Collapsed.Add(KeyOf(node)), node => Collapsed.Remove(KeyOf(node)));

        Add(_filterField, _tree, _emptyLabel);
    }

    /// <summary>The outline shown - what <see cref="Show"/> was last given, before filtering and sorting.</summary>
    internal IReadOnlyList<OutlineNode> Outline => _outline;

    internal TreeView Tree => _tree;
    internal TextField FilterField => _filterField;

    internal OutlineSort Sort
    {
        get => _sort;
        set
        {
            _sort = value;
            Rebuild();
        }
    }

    /// <summary>Goes to a node - unless it was just gone to, which is the same click reported twice.</summary>
    private void Activate(OutlineNode node, bool keepFocus)
    {
        var now = Environment.TickCount64;
        if (_lastActivation is { } last && ReferenceEquals(last.Node, node) && now - last.At < 400)
            return;
        _lastActivation = (node, now);
        NodeActivated?.Invoke(node, keepFocus);
    }

    /// <summary>Shows <paramref name="path"/>'s outline - null for a file it doesn't apply to, or no
    /// file - with whatever was collapsed in that file before.</summary>
    public void Show(string? path, IReadOnlyList<OutlineNode>? outline)
    {
        _path = path;
        _outline = outline ?? [];
        _emptyLabel.Visible = outline is null || outline.Count == 0;
        _emptyLabel.Text = outline is null ? "No outline for this file." : "Nothing to outline yet.";
        Rebuild();
    }

    private void Rebuild()
    {
        var selectedKey = _tree.SelectedObject is { } selected ? KeyOf(selected) : null;
        var scroll = _tree.ScrollOffsetVertical;

        var nodes = DocumentOutline.Sorted(DocumentOutline.Filter(_outline, _filterField.Text), _sort);

        _tree.ClearObjects();
        var built = nodes.Select(n => Build(n, "")).ToList();
        _tree.AddObjects(built);
        _tree.ExpandAll();
        // A filter shows every match; otherwise what was collapsed stays collapsed.
        if (string.IsNullOrWhiteSpace(_filterField.Text))
            foreach (var node in Flatten(built).Where(n => Collapsed.Contains(KeyOf(n))))
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
            Collapsed.Add(KeyOf(node));
            _tree.Collapse(node);
        }
        _tree.SetNeedsDraw();
    }

    public void ExpandAll()
    {
        Collapsed.Clear();
        _tree.ExpandAll();
        _tree.SetNeedsDraw();
    }

    private void ShowContextMenu(Point screenPosition)
    {
        var node = _tree.SelectedObject is TreeNode { Tag: OutlineNode selected } ? selected : null;
        List<View> items = [];
        if (node is not null)
        {
            items.Add(Item("Go To", () => Activate(node, keepFocus: false)));
            items.Add(Item("Find All References", () => FindReferencesRequested?.Invoke(node)));
            items.Add(Item("Rename Symbol...", () => RenameRequested?.Invoke(node)));
            items.Add(new Line());
        }
        foreach (var (sort, text) in new[] { (OutlineSort.Position, "Sort by Position"), (OutlineSort.Name, "Sort by Name"), (OutlineSort.Kind, "Sort by Kind") })
            items.Add(Item((sort == _sort ? "✓ " : "  ") + text, () => Sort = sort));
        items.Add(new Line());
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
