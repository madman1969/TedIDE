using System.Drawing;
using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.DocViewer;

/// <summary>
/// The doc viewer's only window: a category/page tree on the left (built from <see cref="Cc65DocCatalog"/>)
/// and a read-only text pane on the right showing the selected page, converted from its bundled
/// HTML by <see cref="DocTextConverter"/>. Arrow-key browsing the tree (or clicking an entry) swaps
/// the text pane's content immediately as a live preview; pressing Enter on a tree entry
/// additionally moves keyboard focus into the content pane itself (Tab can't do this on its own -
/// see the comment on the tree's Accepted handler below), ready to Tab/Shift+Tab/Enter between its
/// internal links.
///
/// The content pane's internal hyperlinks (see <see cref="DocTextConverter"/>) are followable via
/// <see cref="DocTextView.LinkActivated"/> - <see cref="NavigateToLink"/> selects the target page in
/// the tree (which shows it, via the same path a manual click would) and, if the link named a
/// specific heading, scrolls straight to it.
///
/// Theming is shared with Tedide.App via <c>Tedide.Theming</c>: <c>Program.cs</c> applies the
/// last-saved <see cref="ThemeSettings"/> on startup (the same per-user settings.json Tedide.App
/// reads/writes), and the Theme menu below switches <see cref="ThemeSwitcher"/>'s SchemeManager
/// schemes the same way AppShell's own Theme menu does - every view here (Window, MenuBar,
/// StatusBar, FrameView, TreeView, TextView) resolves its scheme by name at draw time, so no
/// per-view styling code is needed in this class itself.
/// </summary>
public sealed class DocViewerShell : Window
{
    /// <summary>Converted pages, keyed by <see cref="Cc65DocEntry.FileName"/> - filled in the first
    /// time each page is selected, since converting the largest pages (e.g. funcref.html, with well
    /// over a thousand internal links) is noticeable work that a repeat visit - or following a link
    /// back to an already-visited page - shouldn't pay for again.</summary>
    private readonly Dictionary<string, DocConversionResult> _cache = [];

    /// <summary>Every page's tree node, keyed by its <see cref="Cc65DocEntry.FileName"/>, so
    /// <see cref="NavigateToLink"/> can select the right one when the user follows a cross-page link -
    /// keeping the sidebar in sync with whatever a link click just navigated to, the same as it
    /// would be had the user clicked that entry in the tree themselves.</summary>
    private readonly Dictionary<string, TreeNode> _nodesByFileName = [];

    private readonly TreeView _tree = new();
    private readonly DocTextView _contentView = new();
    private readonly FrameView _contentFrame;

    public DocViewerShell()
    {
        Title = "CC65 Documentation Viewer";
        Width = Dim.Fill();
        Height = Dim.Fill();

        var menuBar = BuildMenuBar();
        var statusBar = BuildStatusBar();

        var treeFrame = new FrameView
        {
            Title = "Contents",
            X = 0,
            Y = Pos.Bottom(menuBar),
            Width = Dim.Percent(30),
            Height = Dim.Fill(1),
        };
        _tree.Width = Dim.Fill();
        _tree.Height = Dim.Fill();
        foreach (var category in Cc65DocCatalog.Categories)
        {
            var categoryNode = new TreeNode { Text = category.Name };
            foreach (var entry in category.Entries)
            {
                var entryNode = new TreeNode { Text = entry.FileName, Tag = entry };
                categoryNode.Children.Add(entryNode);
                _nodesByFileName[entry.FileName] = entryNode;
            }
            _tree.AddObject(categoryNode);
        }
        _tree.ExpandAll();
        _tree.SelectionChanged += (_, _) =>
        {
            if (_tree.SelectedObject is TreeNode { Tag: Cc65DocEntry entry })
                ShowDoc(entry);
        };
        // Tab can't do this on its own: it only advances focus among peer views under the same
        // immediate SuperView (see Terminal.Gui's navigation docs), and the tree and content pane
        // each sit alone in their own FrameView, so they're never peers. Enter on a tree item is a
        // deliberate "open this" action (distinct from SelectionChanged, which also fires for mere
        // arrow-key browsing and must NOT steal focus away from the tree mid-browse) - a natural
        // point to move focus into the content pane, ready for its own Tab/Shift+Tab/Enter link
        // navigation.
        _tree.Accepted += (_, _) => _contentView.SetFocus();
        treeFrame.Add(_tree);

        _contentFrame = new FrameView
        {
            Title = "cc65 Documentation",
            X = Pos.Right(treeFrame),
            Y = Pos.Bottom(menuBar),
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        _contentView.Width = Dim.Fill();
        _contentView.Height = Dim.Fill();
        _contentView.WordWrap = true;
        _contentView.Text = "Select a topic on the left to view its documentation.\n\n" +
            "Press Enter on it (or click here) to jump into this pane: Tab / Shift+Tab then move " +
            "between its internal links, Enter follows the one under the caret, and clicking a " +
            "link follows it directly from anywhere.";
        _contentView.LinkActivated += NavigateToLink;
        _contentFrame.Add(_contentView);

        Add([menuBar, treeFrame, _contentFrame, statusBar]);
    }

    private void ShowDoc(Cc65DocEntry entry, string? anchor = null)
    {
        var result = GetOrConvert(entry);

        _contentFrame.Title = $"{entry.FileName} - {entry.Description}";
        _contentView.Links = result.Links;
        _contentView.Spans = result.Spans;
        _contentView.BlockSpans = result.BlockSpans;
        _contentView.Text = result.Text;
        // A freshly loaded page starts scrolled to the top, same as before this had anchors to jump
        // to - unless a link named a specific heading on it, in which case straight to that row.
        var row = anchor is not null && result.AnchorRows.TryGetValue(anchor, out var anchorRow) ? anchorRow : 0;
        _contentView.InsertionPoint = new Point(0, row);
    }

    private DocConversionResult GetOrConvert(Cc65DocEntry entry)
    {
        if (!_cache.TryGetValue(entry.FileName, out var result))
        {
            result = DocTextConverter.Convert(Cc65DocLoader.LoadHtml(entry.FileName), entry.FileName, Cc65DocCatalog.AllFileNames);
            _cache[entry.FileName] = result;
        }
        return result;
    }

    /// <summary>
    /// Follows a link the user just activated in the content pane. Selecting the target page's tree
    /// node (if it isn't already selected) shows it via <see cref="TreeView.SelectionChanged"/>
    /// exactly as a manual click would - including, incidentally, a redundant top-of-page
    /// <see cref="ShowDoc"/> call when the target differs from the current page, immediately
    /// superseded by this method's own call below, which is the one that actually knows about
    /// <see cref="DocLink.TargetAnchor"/>.
    /// </summary>
    private void NavigateToLink(DocLink link)
    {
        if (!Cc65DocCatalog.TryGetEntry(link.TargetFileName, out var entry))
            return;

        if (_nodesByFileName.TryGetValue(link.TargetFileName, out var node))
            _tree.SelectedObject = node;

        ShowDoc(entry, link.TargetAnchor);
    }

    private MenuBar BuildMenuBar()
    {
        var menuBar = new MenuBar();
        var fileMenu = new MenuBarItem("_File", new List<View>
        {
            new MenuItem("_Quit", "", () => Application.RequestStop(this), Key.Q.WithCtrl),
        });

        // Same nine themes, same SchemeManager-based switching, as Tedide.App's own Theme menu
        // (AppShell.BuildMenuBar) - see Tedide.Theming/ThemeSwitcher.cs.
        var themeMenu = new MenuBarItem("_Theme", new List<MenuItem>
        {
            new("VS2026 _Dark", "", () => ThemeSwitcher.Apply(AppTheme.Vs2026Dark), Key.Empty),
            new("VS2026 _Light", "", () => ThemeSwitcher.Apply(AppTheme.Vs2026Light), Key.Empty),
            new("_Borland Turbo C", "", () => ThemeSwitcher.Apply(AppTheme.BorlandTurboC), Key.Empty),
            new("_Monokai", "", () => ThemeSwitcher.Apply(AppTheme.Monokai), Key.Empty),
            new("_Dracula", "", () => ThemeSwitcher.Apply(AppTheme.Dracula), Key.Empty),
            new("Solarized D_ark", "", () => ThemeSwitcher.Apply(AppTheme.SolarizedDark), Key.Empty),
            new("Solarized Li_ght", "", () => ThemeSwitcher.Apply(AppTheme.SolarizedLight), Key.Empty),
            new("_Commodore 64", "", () => ThemeSwitcher.Apply(AppTheme.Commodore64), Key.Empty),
            new("_Amber Phosphor", "", () => ThemeSwitcher.Apply(AppTheme.AmberPhosphor), Key.Empty),
        });

        menuBar.Menus = [fileMenu, themeMenu];
        menuBar.X = 0;
        menuBar.Y = 0;
        menuBar.Width = Dim.Fill();
        return menuBar;
    }

    private StatusBar BuildStatusBar()
    {
        var statusBar = new StatusBar();
        statusBar.Add(new Shortcut(Key.Q.WithCtrl, "~^Q~ Quit", () => Application.RequestStop(this)));
        statusBar.X = 0;
        statusBar.Y = Pos.AnchorEnd(1);
        statusBar.Width = Dim.Fill();
        return statusBar;
    }
}
