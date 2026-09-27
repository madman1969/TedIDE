using Tedide.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

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
    private readonly DocViewerLayoutSettings _layoutSettings = DocViewerLayoutSettings.Load();

    private readonly Dictionary<string, PageEntry> _entriesByFileName = [];
    private readonly Dictionary<string, TreeNode> _nodesByFileName = [];

    private readonly TreeView _tree = new();
    private readonly List<BookNode> _bookNodes = [];
    private readonly FindableMarkdown _contentView = new();
    private readonly FrameView _contentFrame;
    private readonly ThemedMarkdownHighlighter _syntaxHighlighter = new();

    private MenuItem _bookmarksListItem = null!;
    private PageEntry? _currentEntry;

    public DocViewerShell(DocDatabase database)
    {
        _database = database;

        Title = "Tedide Documentation Viewer";
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
            // Fills whatever _contentFrame's current width (the saved percentage, then whatever the
            // user drags it to) doesn't use, so the two panes always exactly share the row, with
            // _contentFrame's own left border acting as the draggable divider - the same set-up as
            // Tedide.App's Solution Explorer/Editor splitter.
            Width = Dim.Fill(Dim.Func(_ => _contentFrame!.Frame.Width)),
            Height = Dim.Fill(1),
        };
        _tree.Width = Dim.Fill();
        _tree.Height = Dim.Fill();
        // Book titles stand out in the theme's highlight colour, like the Solution Explorer's
        // folders; a selected book keeps the tree's own selection look. Read at draw time, so a
        // theme switch restyles them immediately. Every other node gets the tree's scheme
        // explicitly: returning null (documented as "use the default") draws white-on-black
        // whatever the theme.
        _tree.ColorGetter = node => node is BookNode ? BookScheme(_tree.GetScheme()) : _tree.GetScheme();
        foreach (var book in _database.LoadCatalog())
        {
            var bookNode = new BookNode { Text = book.Name };
            foreach (var category in book.Categories)
            {
                var categoryNode = new TreeNode { Text = category.Name };
                foreach (var entry in category.Entries)
                {
                    var entryNode = new TreeNode { Text = entry.FileName, Tag = entry };
                    categoryNode.Children.Add(entryNode);
                    _nodesByFileName[entry.FileName] = entryNode;
                    _entriesByFileName[entry.FileName] = entry;
                }
                bookNode.Children.Add(categoryNode);
            }
            _tree.AddObject(bookNode);
            _bookNodes.Add(bookNode);
        }
        _tree.ExpandAll();
        // Books start expanded unless the user collapsed them last time - recording the collapsed
        // ones (not the expanded ones) means a book added to Docs.db later still shows up open.
        foreach (var bookNode in _bookNodes.Where(b => _layoutSettings.CollapsedBooks.Contains(b.Text)))
            _tree.Collapse(bookNode);
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
            Title = "Documentation",
            // Page names are full of underscores (The C Book's "answers/chapter_7") and a title reads
            // its first "_" as a hotkey marker - it was shown as "answers/chapter7".
            HotKeySpecifier = new System.Text.Rune(0xFFFF),
            X = Pos.Right(treeFrame),
            Y = Pos.Bottom(menuBar),
            Width = Dim.Percent(_layoutSettings.ClampedContentPaneWidthPercent),
            Height = Dim.Fill(1),
            // Makes this frame's left border a draggable divider between it and the Contents tree
            // (treeFrame's Width, above, tracks this frame's Frame.Width live). CanFocus is required
            // for the border-drag mouse interaction to register.
            Arrangement = ViewArrangement.LeftResizable,
            CanFocus = true,
        };
        // treeFrame's Width reads _contentFrame.Frame.Width live, but within one layout pass
        // treeFrame is resolved first, so it sees the width from before this pass - on the first
        // pass, 0, claiming the whole window. A layout pass queued for strictly after the current
        // one lets it catch up (a synchronous SetNeedsLayout here is superseded by the pass it's
        // called from) - see AppShell's _editorFrame.FrameChanged for the full story.
        _contentFrame.FrameChanged += (_, _) => Application.AddTimeout(TimeSpan.Zero, () =>
        {
            SetNeedsLayout();
            return false;
        });
        _contentView.Width = Dim.Fill();
        _contentView.Height = Dim.Fill();
        _contentView.ViewportSettings = ViewportSettingsFlags.HasScrollBars;
        // Real per-token syntax highlighting in fenced code (via TextMateSharp's VS Code grammars),
        // with headings/links/etc. styled from the app theme - see ThemedMarkdownHighlighter.
        _contentView.SyntaxHighlighter = _syntaxHighlighter;
        // Otherwise the whole pane is filled with the TextMate theme's own editor background (pure
        // white for Light+, near-black for Dark+) instead of the app theme's - Solarized Light's
        // cream, Commodore 64's blue - so the pane stood out from everything around it.
        _contentView.UseThemeBackground = false;
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
            // Strips only the ".html" suffix (not Path.GetFileNameWithoutExtension, which would
            // also drop a leading directory) - a cc65 manual link is always a bare "file.html" so
            // this is unchanged for it, but The C Book's own FileName ids are chapter-qualified
            // relative paths (e.g. "chapter5/pointers"), and its links carry that same directory
            // ("chapter5/pointers.html") to stay unique - see CBookHtmlToMarkdownConverter.
            var fileName = filePart.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                ? filePart[..^".html".Length]
                : filePart;

            e.Handled = true;
            NavigateTo(fileName, anchor, pushHistory: true);
        };
        // FindableMarkdown (see that class) keeps "Find..." on the content pane's right-click
        // context menu across the base Markdown view's own from-scratch rebuilds of it.
        _contentView.FindRequested += ShowPageFind;
        _contentFrame.Add(_contentView);

        Add([menuBar, treeFrame, _contentFrame, statusBar]);
    }

    /// <summary>A book's top-level node in the Contents tree - drawn with <see cref="BookScheme"/>.</summary>
    internal sealed class BookNode : TreeNode;

    /// <summary>
    /// <paramref name="treeScheme"/> with unselected text in the theme's highlight colour - the same
    /// look as Tedide.App's Solution Explorer folders (see <see cref="TreeNodeSchemes.Emphasised"/>).
    /// </summary>
    internal static Scheme BookScheme(Scheme treeScheme) => TreeNodeSchemes.Emphasised(treeScheme);

    /// <summary>
    /// Records the Contents/Documentation divider's current position (as a percentage of the
    /// window's width) and which books are collapsed in the tree, so the next run starts where
    /// this one left off. Called once, from Program.cs, right after <c>Application.Run(shell)</c>
    /// returns - i.e. when the user quits.
    /// </summary>
    public void SaveLayoutSettings()
    {
        if (Frame.Width > 0)
            _layoutSettings.ContentPaneWidthPercent = _contentFrame.Frame.Width * 100 / Frame.Width;
        _layoutSettings.CollapsedBooks = _bookNodes.Where(b => !_tree.IsExpanded(b)).Select(b => b.Text).ToList();
        _layoutSettings.Save();
    }

    /// <summary>Picks light or dark TextMate token colors for code blocks to match whichever of the
    /// app's own themes is currently active - called once at startup and again on every
    /// <see cref="ThemeSwitcher.Changed"/> (theme menu selection).</summary>
    private void UpdateSyntaxHighlighterTheme()
    {
        _syntaxHighlighter.MatchBackground(SchemeManager.GetScheme("Base").Normal.Background);
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
            ToggleAndSave(entry.FileName, null, "");
            return;
        }

        var dialog = new AddBookmarkDialog(entry.Description);
        Application.Run(dialog);
        if (dialog.Label is { } label)
            ToggleAndSave(entry.FileName, null, label);
    }

    /// <summary>
    /// Toggles the bookmark and saves the list. A save that fails (the bookmarks file read-only,
    /// locked, or its folder not writable) is reported rather than thrown: this runs from Ctrl+D
    /// and the Bookmarks menu, where an escaping exception ended the whole app (confirmed live).
    /// The change still applies for this session - only remembering it for the next one failed.
    /// </summary>
    private void ToggleAndSave(string fileName, string? anchor, string label)
    {
        try
        {
            _bookmarks.Toggle(fileName, anchor, label);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Serilog.Log.Error(ex, "Could not save bookmarks");
            TedideMessageBox.ErrorQuery("Could Not Save Bookmarks",
                $"The bookmark was changed for this session, but saving it failed:\n{ex.Message}", "OK");
        }
        RefreshBookmarksMenu();
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

        // Numbered "_1 label", like Tedide.App's Recent Projects menu: the numeral takes the one
        // "_" a menu title reads as its hotkey marker, so an underscore in the label itself (often
        // a page description or name, e.g. "chapter_7") shows literally instead of being swallowed.
        return _bookmarks.Items
            .Select((b, i) => new MenuItem($"_{i + 1} {b.Label}", b.FileName, () => OpenBookmark(b), Key.Empty).WithLiteralHelpText())
            .ToList();
    }

    /// <summary>
    /// Navigates to a bookmark - or, if its page is no longer in Docs.db (a rebuilt database can
    /// rename or drop pages), says so and offers to remove it. Choosing one used to silently do
    /// nothing, and it could never be removed either: Add/Remove only acts on the page on screen.
    /// </summary>
    private void OpenBookmark(Bookmark bookmark)
    {
        if (_entriesByFileName.ContainsKey(bookmark.FileName))
        {
            NavigateTo(bookmark.FileName, bookmark.Anchor, pushHistory: true);
            return;
        }

        var choice = TedideMessageBox.ErrorQuery("Page Not Found",
            $"This bookmark points to '{bookmark.FileName}', which isn't in this version of the documentation.",
            "_Remove Bookmark", "_Keep");
        if (choice == 0)
            ToggleAndSave(bookmark.FileName, bookmark.Anchor, bookmark.Label);
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
        statusBar.Add(new Shortcut(Key.Q.WithCtrl, "Quit", () => Application.RequestStop(this)));
        statusBar.Add(new Shortcut(Key.F.WithCtrl, "Search", ShowSearchDialog));
        statusBar.X = 0;
        statusBar.Y = Pos.AnchorEnd(1);
        statusBar.Width = Dim.Fill();
        return statusBar;
    }
}
