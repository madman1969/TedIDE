using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Tedide.App.Views;

/// <summary>
/// The one-row strip of open files above the editor: each tab shows the file's name, a "*" when
/// it has unsaved changes, and an "x" to close it; the active tab is drawn in the Accent colour.
/// A click selects a tab, a click on its "x" (or a middle-click anywhere on it) closes it, and the
/// mouse wheel moves between tabs. When the tabs don't all fit, the strip scrolls to keep the
/// active one in view, with "&lt;"/"&gt;" showing there are more either side. It only draws and
/// reports clicks - <see cref="EditorPane"/> owns the documents.
/// </summary>
public sealed class DocumentTabStrip : View
{
    public sealed record Tab(string Title, bool IsModified);

    private IReadOnlyList<Tab> _tabs = [];
    private int _active = -1;
    /// <summary>Where each drawn tab landed, for turning a click back into a tab.</summary>
    private readonly List<(int Start, int End, int CloseColumn, int Index)> _hitAreas = [];

    public event Action<int>? TabSelected;
    public event Action<int>? TabCloseRequested;

    /// <summary>Raised by the mouse wheel: +1 for the next tab, -1 for the previous one.</summary>
    public event Action<int>? TabCycleRequested;

    public DocumentTabStrip()
    {
        Height = 1;
        Width = Dim.Fill();
    }

    /// <summary>
    /// Text shown right-aligned in whatever room the tabs leave - the git branch and who last
    /// changed the caret's line (the status bar had no room for it). Shortened to fit, and left out
    /// when there's barely any room.
    /// </summary>
    public string Annotation
    {
        get => _annotation;
        set
        {
            if (_annotation == value)
                return;
            _annotation = value;
            SetNeedsDraw();
        }
    }

    private string _annotation = "";

    /// <summary>The part of <paramref name="annotation"/> that fits in <paramref name="room"/>
    /// columns, ending in "…" when cut; empty when there's too little room to be worth showing.</summary>
    internal static string Fit(string annotation, int room) =>
        room < 8 || annotation.Length == 0 ? ""
        : annotation.Length <= room ? annotation
        : annotation[..(room - 1)] + "…";

    public void SetTabs(IReadOnlyList<Tab> tabs, int active)
    {
        _tabs = tabs;
        _active = active;
        SetNeedsDraw();
    }

    /// <summary>A tab's text: " name* x " - the "*" only when modified.</summary>
    internal static string Label(Tab tab) => $" {tab.Title}{(tab.IsModified ? "*" : "")} x ";

    /// <summary>
    /// The first tab to draw so that <paramref name="active"/> fits in <paramref name="width"/>
    /// columns, leaving room for the scroll markers - 0 whenever everything up to it fits.
    /// </summary>
    internal static int FirstVisible(IReadOnlyList<Tab> tabs, int active, int width)
    {
        if (active < 0)
            return 0;
        var first = 0;
        // Two columns are kept back for the "<" and ">" markers.
        while (first < active && tabs.Skip(first).Take(active - first + 1).Sum(t => Label(t).Length + 1) > width - 2)
            first++;
        return first;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var normal = GetAttributeForRole(VisualRole.Normal);
        SetAttribute(normal);
        AddStr(0, 0, new string(' ', Math.Max(0, width)));
        _hitAreas.Clear();
        if (_tabs.Count == 0)
        {
            DrawAnnotation(0, width);
            return true;
        }

        var first = FirstVisible(_tabs, _active, width);
        var x = 0;
        if (first > 0)
        {
            AddStr(0, 0, "<");
            x = 1;
        }

        var accent = SchemeManager.GetScheme("Accent").Normal;
        for (var i = first; i < _tabs.Count; i++)
        {
            var label = Label(_tabs[i]);
            if (x + label.Length > width - (i < _tabs.Count - 1 ? 1 : 0))
            {
                SetAttribute(normal);
                AddStr(Math.Max(0, width - 1), 0, ">");
                break;
            }

            SetAttribute(i == _active ? accent : normal);
            AddStr(x, 0, label);
            _hitAreas.Add((x, x + label.Length, x + label.Length - 2, i));
            x += label.Length;
            SetAttribute(normal);
            AddStr(x, 0, "|");
            x++;
        }
        DrawAnnotation(x, width);
        return true;
    }

    /// <summary>Draws <see cref="Annotation"/> right-aligned after column <paramref name="tabsEnd"/>,
    /// dimmed so it doesn't compete with the tabs.</summary>
    private void DrawAnnotation(int tabsEnd, int width)
    {
        var text = Fit(_annotation, width - tabsEnd - 3);
        if (text.Length == 0)
            return;
        SetAttribute(GetAttributeForRole(VisualRole.Disabled));
        AddStr(width - text.Length - 1, 0, text);
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.IsWheel)
        {
            TabCycleRequested?.Invoke(mouse.Flags.FastHasFlags(MouseFlags.WheeledDown) ? 1 : -1);
            return true;
        }

        if (mouse.Position is not { } position)
            return base.OnMouseEvent(mouse);

        var hit = _hitAreas.FirstOrDefault(h => position.X >= h.Start && position.X < h.End);
        if (hit.End == 0)
            return base.OnMouseEvent(mouse);

        if (mouse.Flags.FastHasFlags(MouseFlags.MiddleButtonClicked)
            || (mouse.Flags.FastHasFlags(MouseFlags.LeftButtonClicked) && position.X == hit.CloseColumn))
        {
            TabCloseRequested?.Invoke(hit.Index);
            return true;
        }
        if (mouse.Flags.FastHasFlags(MouseFlags.LeftButtonClicked))
        {
            TabSelected?.Invoke(hit.Index);
            return true;
        }
        return base.OnMouseEvent(mouse);
    }
}
