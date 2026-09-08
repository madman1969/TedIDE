using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Tedide.DocViewer;

/// <summary>
/// The doc viewer's only window: a category/page tree on the left (built from <see cref="Cc65DocCatalog"/>)
/// and a read-only text pane on the right showing the selected page, converted from its bundled
/// HTML by <see cref="DocTextConverter"/>. Selecting a different page (arrow keys or mouse, no need
/// to press Enter) swaps the text pane's content immediately.
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
    /// <summary>Converted page text, keyed by <see cref="Cc65DocEntry.FileName"/> - filled in the
    /// first time each page is selected, since converting the largest pages (e.g. funcref.html) is
    /// noticeable work that a repeat visit shouldn't pay for again.</summary>
    private readonly Dictionary<string, string> _convertedTextCache = [];

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
                categoryNode.Children.Add(new TreeNode { Text = entry.FileName, Tag = entry });
            _tree.AddObject(categoryNode);
        }
        _tree.ExpandAll();
        _tree.SelectionChanged += (_, _) =>
        {
            if (_tree.SelectedObject is TreeNode { Tag: Cc65DocEntry entry })
                ShowDoc(entry);
        };
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
        _contentView.Text = "Select a topic on the left to view its documentation.";
        _contentFrame.Add(_contentView);

        Add([menuBar, treeFrame, _contentFrame, statusBar]);
    }

    private void ShowDoc(Cc65DocEntry entry)
    {
        if (!_convertedTextCache.TryGetValue(entry.FileName, out var text))
        {
            text = DocTextConverter.ToPlainText(Cc65DocLoader.LoadHtml(entry.FileName));
            _convertedTextCache[entry.FileName] = text;
        }

        _contentFrame.Title = $"{entry.FileName} - {entry.Description}";
        _contentView.Text = text;
        // A freshly loaded page should always start scrolled to the top, not wherever the
        // previous page happened to leave the caret/viewport.
        _contentView.InsertionPoint = new System.Drawing.Point(0, 0);
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
