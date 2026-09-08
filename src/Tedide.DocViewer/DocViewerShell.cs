using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TextMateSharp.Grammars;

namespace Tedide.DocViewer;

/// <summary>
/// The doc viewer's only window: a category/page tree on the left (from <see cref="DocDatabase.LoadCatalog"/>)
/// and a native <see cref="Terminal.Gui.Views.Markdown"/> pane on the right showing the selected
/// page's Markdown (converted from cc65's HTML manuals by <c>tools/Cc65DocsDbBuilder</c>, once,
/// ahead of time - not at runtime; see <see cref="DocDatabase"/>). Arrow-key browsing the tree (or
/// clicking an entry) swaps the content pane immediately as a live preview; pressing Enter on a tree
/// entry additionally moves keyboard focus into the content pane itself (Tab can't do this on its
/// own - see the comment on the tree's Accepted handler below).
///
/// The Markdown view's own built-in link highlighting/Tab-cycling/mouse-click handling does almost
/// all of the work an earlier hand-rolled TextView-based version of this had to implement itself -
/// <see cref="Terminal.Gui.Views.Markdown.LinkClicked"/> only needs to step in for a *cross-page*
/// link (<see cref="NavigateTo"/>); a same-page <c>#slug</c> link is already auto-scrolled to by the
/// view itself (confirmed by direct testing - see this project's Markdown view usage notes).
///
/// <see cref="NavigationHistory"/> and <see cref="DocBookmarks"/> add browser-style Back/Forward and
/// saved-location bookmarks on top; <see cref="SearchDialog"/> adds full-text search over every
/// page via Docs.db's FTS5 index.
///
/// Theming is shared with Tedide.App via <c>Tedide.Theming</c>: <c>Program.cs</c> applies the
/// last-saved <see cref="ThemeSettings"/> on startup (the same per-user settings.json Tedide.App
/// reads/writes), and the Theme menu below switches <see cref="ThemeSwitcher"/>'s SchemeManager
/// schemes the same way AppShell's own Theme menu does.
/// </summary>
public sealed class DocViewerShell : Window
{
    private readonly DocDatabase _database;
    private readonly NavigationHistory _history = new();
    private readonly DocBookmarks _bookmarks = DocBookmarks.Load();

    private readonly Dictionary<string, PageEntry> _entriesByFileName = [];
    private readonly Dictionary<string, TreeNode> _nodesByFileName = [];

    private readonly TreeView _tree = new();
    private readonly FindableMarkdown _contentView = new();
    private readonly FrameView _contentFrame;
    private readonly TextMateSyntaxHighlighter _syntaxHighlighter = new();

    private MenuItem _bookmarksListItem = null!;
    private PageEntry? _currentEntry;

    public DocViewerShell(DocDatabase database)
    {
        _database = database;

        Title = "CC65 Documentation Viewer";
        Width = Dim.Fill();
        Height = Dim.Fill();

        var menuBar = BuildMenuBar();
        var statusBar = BuildStatusBar();

        // Keys with no default binding anywhere (Tab/arrows are already claimed by the tree/content
        // pane's own navigation) bubble up to the focused view's ancestors when nothing else handles
        // them, which is what lets these window-level bindings work regardless of which pane
        // currently has focus - unlike a MenuItem's own Key, which is only a live shortcut while
        // that menu is actually open.
        KeyDown += (_, key) =>
        {
            if (key == Key.CursorLeft.WithAlt) { GoBack(); key.Handled = true; }
            else if (key == Key.CursorRight.WithAlt) { GoForward(); key.Handled = true; }
            else if (key == Key.F.WithCtrl) { ShowSearchDialog(); key.Handled = true; }
            else if (key == Key.D.WithCtrl) { ToggleBookmark(); key.Handled = true; }
        };

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
        foreach (var category in _database.LoadCatalog())
        {
            var categoryNode = new TreeNode { Text = category.Name };
            foreach (var entry in category.Entries)
            {
                var entryNode = new TreeNode { Text = entry.FileName, Tag = entry };
                categoryNode.Children.Add(entryNode);
                _nodesByFileName[entry.FileName] = entryNode;
                _entriesByFileName[entry.FileName] = entry;
            }
            _tree.AddObject(categoryNode);
        }
        _tree.ExpandAll();
        _tree.SelectionChanged += (_, _) =>
        {
            if (_tree.SelectedObject is TreeNode { Tag: PageEntry entry })
                ShowDoc(entry);
        };
        // Tab can't move focus here on its own: it only advances among peer views under the same
        // immediate SuperView (see Terminal.Gui's navigation docs), and the tree and content pane
        // each sit alone in their own FrameView, so they're never peers. Enter on a tree item is a
        // deliberate "open this" action (distinct from SelectionChanged, which also fires for mere
        // arrow-key browsing and must NOT steal focus away from the tree mid-browse) - a natural
        // point to both move focus into the content pane and record the visit in history.
        _tree.Accepted += (_, _) =>
        {
            if (_tree.SelectedObject is TreeNode { Tag: PageEntry entry })
                _history.Push(new NavigationEntry(entry.FileName, null));
            _contentView.SetFocus();
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
        _contentView.ViewportSettings = ViewportSettingsFlags.HasScrollBars;
        // Real per-token syntax highlighting (via TextMateSharp's VS Code grammars) instead of the
        // plain dimmed code-block background Markdown falls back to with no highlighter set - makes
        // fenced code genuinely stand out rather than just visually separated from prose. Tracks
        // whichever of the app's own themes is active (see UpdateSyntaxHighlighterTheme) rather than
        // a fixed light/dark choice.
        _contentView.SyntaxHighlighter = _syntaxHighlighter;
        UpdateSyntaxHighlighterTheme();
        ThemeSwitcher.Changed += UpdateSyntaxHighlighterTheme;
        _contentView.Text = "Select a topic on the left to view its documentation.\n\n" +
            "Press Enter on it (or click here) to jump into this pane: Tab / Shift+Tab move between " +
            "its internal links, Enter follows the one under the caret, and clicking a link follows " +
            "it directly from anywhere.\n\n" +
            "Alt+Left / Alt+Right go back/forward, Ctrl+D bookmarks the current page, and Ctrl+F " +
            "searches every page's text.";
        _contentView.LinkClicked += (_, e) =>
        {
            if (e.Url.StartsWith('#'))
                return; // a same-page anchor - the view's own default handling scrolls to it already.

            var hashIndex = e.Url.IndexOf('#');
            var filePart = hashIndex >= 0 ? e.Url[..hashIndex] : e.Url;
            var anchor = hashIndex >= 0 ? e.Url[(hashIndex + 1)..] : null;
            var fileName = Path.GetFileNameWithoutExtension(filePart);

            e.Handled = true;
            NavigateTo(fileName, anchor, pushHistory: true);
        };
        // FindableMarkdown (see that class) keeps "Find..." on the content pane's right-click
        // context menu across the base Markdown view's own from-scratch rebuilds of it.
        _contentView.FindRequested += ShowPageFind;
        _contentFrame.Add(_contentView);

        Add([menuBar, treeFrame, _contentFrame, statusBar]);
    }

    /// <summary>Picks a light or dark TextMate theme for <see cref="_syntaxHighlighter"/> matching
    /// whichever of the app's own themes is currently active, so code blocks read naturally against
    /// both e.g. Vs2026Light and the mostly-dark remaining themes - called once at startup and again
    /// on every <see cref="ThemeSwitcher.Changed"/> (theme menu selection).</summary>
    private void UpdateSyntaxHighlighterTheme()
    {
        var editorBackground = SchemeManager.GetScheme("Base").Normal.Background;
        _syntaxHighlighter.SetTheme(TextMateSyntaxHighlighter.GetThemeForBackground(editorBackground));
        _contentView.SetNeedsDraw();
    }

    /// <summary>Displays a page (optionally scrolled to one of its headings) without touching
    /// <see cref="_history"/> - the single rendering path every navigation action (tree selection,
    /// a followed link, Back/Forward, a bookmark, a search result) ultimately calls.</summary>
    private void ShowDoc(PageEntry entry, string? anchor = null)
    {
        _currentEntry = entry;
        _contentFrame.Title = $"{entry.FileName} - {entry.Description}";
        _contentView.Text = _database.GetMarkdown(entry.FileName);
        if (anchor is not null)
            _contentView.ScrollToAnchor(anchor);
        else
            _contentView.Viewport = _contentView.Viewport with { X = 0, Y = 0 };
    }

    /// <summary>Shows a page, syncing the tree's own selection to match (so the sidebar highlight
    /// always reflects whatever a link/bookmark/search result/Back-Forward just navigated to, the
    /// same as clicking that entry in the tree directly would), and records the visit in
    /// <see cref="_history"/> unless <paramref name="pushHistory"/> is false (Back/Forward navigate
    /// *within* existing history rather than extending it).</summary>
    private void NavigateTo(string fileName, string? anchor, bool pushHistory)
    {
        if (!_entriesByFileName.TryGetValue(fileName, out var entry))
            return;

        if (_nodesByFileName.TryGetValue(fileName, out var node))
            _tree.SelectedObject = node; // may also call ShowDoc via SelectionChanged - harmless, superseded below.

        ShowDoc(entry, anchor);
        if (pushHistory)
            _history.Push(new NavigationEntry(fileName, anchor));
    }

    private void GoBack()
    {
        if (_history.GoBack() is { } entry)
            NavigateTo(entry.FileName, entry.Anchor, pushHistory: false);
    }

    private void GoForward()
    {
        if (_history.GoForward() is { } entry)
            NavigateTo(entry.FileName, entry.Anchor, pushHistory: false);
    }

    private void ShowSearchDialog()
    {
        var dialog = new SearchDialog(_database);
        Application.Run(dialog);
        if (dialog.SelectedResult is { } result)
            NavigateTo(result.FileName, null, pushHistory: true);
    }

    /// <summary>Opens the in-page Find dialog (see <see cref="PageFindDialog"/>), from the content
    /// pane's right-click context menu - distinct from Ctrl+F's <see cref="ShowSearchDialog"/>,
    /// which searches every page rather than just the one currently shown.</summary>
    private void ShowPageFind() => Application.Run(new PageFindDialog(_contentView));

    /// <summary>Opens the Help > About dialog. Read-only - see <see cref="AboutDialog"/>.</summary>
    private void ShowAbout()
    {
        Application.Run(new AboutDialog());
    }

    /// <summary>Adds a bookmark for the current page (prompting for a label via
    /// <see cref="AddBookmarkDialog"/>), or removes it without prompting if one already exists -
    /// bookmarks don't yet track a specific heading, only the page itself.</summary>
    private void ToggleBookmark()
    {
        if (_currentEntry is not { } entry)
            return;

        if (_bookmarks.Contains(entry.FileName, null))
        {
            _bookmarks.Toggle(entry.FileName, null, "");
            RefreshBookmarksMenu();
            return;
        }

        var dialog = new AddBookmarkDialog(entry.Description);
        Application.Run(dialog);
        if (dialog.Label is { } label)
        {
            _bookmarks.Toggle(entry.FileName, null, label);
            RefreshBookmarksMenu();
        }
    }

    private MenuBar BuildMenuBar()
    {
        var menuBar = new MenuBar();
        var fileMenu = new MenuBarItem("_File", new List<View>
        {
            new MenuItem("_Quit", "", () => Application.RequestStop(this), Key.Q.WithCtrl),
        });

        var navigateMenu = new MenuBarItem("_Navigate", new List<View>
        {
            new MenuItem("_Back", "", GoBack, Key.CursorLeft.WithAlt),
            new MenuItem("_Forward", "", GoForward, Key.CursorRight.WithAlt),
            new Line(),
            new MenuItem("_Search Documentation...", "", ShowSearchDialog, Key.F.WithCtrl),
        });

        _bookmarksListItem = new MenuItem("_Saved Bookmarks", "", new Menu(BuildBookmarkMenuItems()));
        var bookmarksMenu = new MenuBarItem("_Bookmarks", new List<View>
        {
            new MenuItem("_Add/Remove Bookmark for Current Page", "", ToggleBookmark, Key.D.WithCtrl),
            new Line(),
            _bookmarksListItem,
        });

        // Shared with Tedide.App (ThemeMenuBuilder, in Tedide.Theming) - same nine entries, same
        // SchemeManager-based switching, and the currently active one is marked with a leading
        // checkmark, kept live via ThemeSwitcher.Changed.
        var themeMenu = ThemeMenuBuilder.Build();

        var helpMenu = new MenuBarItem("_Help", new List<MenuItem>
        {
            new("_About Tedide DocViewer...", "", ShowAbout, Key.Empty),
        });

        menuBar.Menus = [fileMenu, navigateMenu, bookmarksMenu, themeMenu, helpMenu];
        menuBar.X = 0;
        menuBar.Y = 0;
        menuBar.Width = Dim.Fill();
        return menuBar;
    }

    private List<MenuItem> BuildBookmarkMenuItems()
    {
        if (_bookmarks.Items.Count == 0)
            return [new MenuItem("(No Bookmarks)", "", () => { }, Key.Empty)];

        return _bookmarks.Items
            .Select(b => new MenuItem(b.Label, b.FileName, () => NavigateTo(b.FileName, b.Anchor, pushHistory: true), Key.Empty))
            .ToList();
    }

    /// <summary>Repopulates the "Saved Bookmarks" submenu in place - deferred the same way (and for
    /// the same reason) as AppShell's own RefreshRecentProjectsMenu: this runs from inside a click
    /// handler on the very menu tree still being torn down by that click's in-progress close
    /// sequence, so the rebuild has to wait for that to finish first.</summary>
    private void RefreshBookmarksMenu()
    {
        Application.AddTimeout(TimeSpan.Zero, () =>
        {
            var menu = _bookmarksListItem.SubMenu!;
            menu.RemoveAll();
            foreach (var item in BuildBookmarkMenuItems())
                menu.Add(item);
            return false;
        });
    }

    private StatusBar BuildStatusBar()
    {
        var statusBar = new StatusBar();
        statusBar.Add(new Shortcut(Key.Q.WithCtrl, "~^Q~ Quit", () => Application.RequestStop(this)));
        statusBar.Add(new Shortcut(Key.F.WithCtrl, "~^F~ Search", ShowSearchDialog));
        statusBar.X = 0;
        statusBar.Y = Pos.AnchorEnd(1);
        statusBar.Width = Dim.Fill();
        return statusBar;
    }
}
