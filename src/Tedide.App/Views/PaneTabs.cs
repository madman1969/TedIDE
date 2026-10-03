using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Tedide.App.Views;

/// <summary>
/// A bordered group of windows sharing one space, with a one-row strip of their names on top -
/// the selected one in the Accent colour and the only one shown. A click on a name, or Left/Right
/// while the strip has focus, switches window. Used by <see cref="DebugPanelView"/> rather than
/// Terminal.Gui's own <c>Tabs</c>: a <c>Tabs</c> nested inside the bottom pane's <c>Tabs</c> never
/// gets mouse clicks - not on its headers, not on its contents.
/// </summary>
public sealed class PaneTabs : View
{
    private readonly IReadOnlyList<(string Title, View Page)> _pages;
    private readonly Strip _strip;

    public int Selected { get; private set; }

    public PaneTabs(params (string Title, View Page)[] pages)
    {
        BorderStyle = LineStyle.Single;
        // A column clear of the border either side, and a blank row under the strip.
        Padding.Thickness = new Thickness(1, 0, 1, 0);
        _pages = pages;
        _strip = new Strip(this) { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        Add(_strip);
        foreach (var (_, page) in pages)
        {
            page.X = 0;
            page.Y = 2;
            page.Width = Dim.Fill();
            page.Height = Dim.Fill();
            Add(page);
        }
        Select(0);
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _pages.Count)
            return;
        var hadFocus = _pages[Selected].Page.HasFocus;
        Selected = index;
        for (var i = 0; i < _pages.Count; i++)
            _pages[i].Page.Visible = i == index;
        if (hadFocus)
            _pages[index].Page.SetFocus();
        _strip.SetNeedsDraw();
    }

    /// <summary>Where each name lands in the strip: " Locals │ Watch " - the end is exclusive.</summary>
    internal static IReadOnlyList<(int Start, int End)> TitleAreas(IEnumerable<string> titles)
    {
        var areas = new List<(int, int)>();
        var x = 0;
        foreach (var title in titles)
        {
            areas.Add((x, x + title.Length + 2));
            x += title.Length + 3;
        }
        return areas;
    }

    private sealed class Strip : View
    {
        private readonly PaneTabs _owner;

        public Strip(PaneTabs owner)
        {
            _owner = owner;
            CanFocus = true;
        }

        protected override bool OnDrawingContent(DrawContext? context)
        {
            var normal = GetAttributeForRole(VisualRole.Normal);
            var selected = HasFocus ? GetAttributeForRole(VisualRole.Focus) : SchemeManager.GetScheme("Accent").Normal;
            SetAttribute(normal);
            AddStr(0, 0, new string(' ', Math.Max(0, Viewport.Width)));
            var areas = TitleAreas(_owner._pages.Select(p => p.Title));
            for (var i = 0; i < areas.Count; i++)
            {
                var (start, end) = areas[i];
                if (end > Viewport.Width)
                    break;
                SetAttribute(i == _owner.Selected ? selected : normal);
                AddStr(start, 0, $" {_owner._pages[i].Title} ");
                SetAttribute(normal);
                if (i < areas.Count - 1)
                    AddStr(end, 0, "│");
            }
            return true;
        }

        protected override bool OnMouseEvent(Mouse mouse)
        {
            if (mouse.Flags.FastHasFlags(MouseFlags.LeftButtonClicked) && mouse.Position is { } position)
            {
                var areas = TitleAreas(_owner._pages.Select(p => p.Title));
                var index = areas.ToList().FindIndex(a => position.X >= a.Start && position.X < a.End);
                if (index >= 0)
                {
                    _owner.Select(index);
                    return true;
                }
            }
            return base.OnMouseEvent(mouse);
        }

        protected override bool OnKeyDown(Key key)
        {
            if (key == Key.CursorLeft || key == Key.CursorRight)
            {
                var count = _owner._pages.Count;
                _owner.Select((_owner.Selected + (key == Key.CursorRight ? 1 : count - 1)) % count);
                return true;
            }
            return base.OnKeyDown(key);
        }
    }
}
