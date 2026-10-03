using System.Drawing;
using Tedide.Git;
using Tedide.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Rendering;
using Terminal.Gui.ViewBase;
using Color = Terminal.Gui.Drawing.Color;
using GuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace Tedide.App.Views;

/// <summary>
/// Git phase 2: VS Code's change bars in the editor's gutter - green for added lines, blue for
/// modified ones, a red bar above (or, at the end of the file, below) where lines were removed.
/// They sit in the blank column between the line numbers and the fold indicators.
/// <para>
/// Terminal.Gui.Editor's gutter has no extension point - its line-number view is internal, and so
/// is the editor's row-to-line mapping (folding and word wrap make it more than Viewport.Y + row).
/// So this is registered as a background renderer, which the editor calls with each visual line
/// and its row as it draws the text - recording which line is on which row and drawing nothing -
/// and it paints the bars from the gutter's own DrawComplete. That's safe because a view draws its
/// content before its adornments' subviews (the gutter lives in the editor's Padding), so the rows
/// recorded are this frame's.
/// </para>
/// </summary>
public sealed class GitLineMarkers : IBackgroundRenderer
{
    private static readonly Color AddedHue = new(0x73, 0xC9, 0x91);
    private static readonly Color ModifiedHue = new(0x4F, 0xA8, 0xE0);
    private static readonly Color RemovedHue = new(0xE4, 0x67, 0x6B);

    private readonly Editor _editor;
    private readonly Dictionary<int, int> _lineByRow = [];
    private IReadOnlyDictionary<int, LineChangeKind> _changes = new Dictionary<int, LineChangeKind>();
    private Gutter? _gutter;

    public GitLineMarkers(Editor editor) => _editor = editor;

    /// <summary>The shown file's changed lines, by 1-based number - empty for none, or no git.</summary>
    public IReadOnlyDictionary<int, LineChangeKind> Changes
    {
        get => _changes;
        set
        {
            if (value.Count == 0 && _changes.Count == 0)
                return;
            _changes = value;
            _editor.SetNeedsDraw();
            _gutter?.SetNeedsDraw();
        }
    }

    public void Draw(View host, CellVisualLine line, int row, Rectangle viewport)
    {
        if (row == 0)
        {
            _lineByRow.Clear();
            HookGutter(host);
        }
        // A wrapped line's continuation rows start partway into it; only its first row gets a bar.
        var firstRow = line.Elements.Count == 0 || line.Elements[0].DocumentOffset <= line.DocumentLine.Offset;
        if (firstRow)
            _lineByRow[row] = line.DocumentLine.LineNumber;
        else
            _lineByRow.Remove(row);
    }

    /// <summary>The editor creates its gutter lazily, and again after the View menu turns the gutter
    /// off and on - so look for it each frame, and follow a new one.</summary>
    private void HookGutter(View host)
    {
        var gutter = host.Padding.GetOrCreateView().SubViews.OfType<Gutter>().FirstOrDefault();
        if (ReferenceEquals(gutter, _gutter))
            return;
        if (_gutter is not null)
            _gutter.DrawComplete -= OnGutterDrawn;
        _gutter = gutter;
        if (_gutter is not null)
        {
            _gutter.DrawComplete += OnGutterDrawn;
            _gutter.SetNeedsDraw();
        }
    }

    private void OnGutterDrawn(object? sender, DrawEventArgs e)
    {
        if (_gutter is not { } gutter || _changes.Count == 0 || _editor.Document is not { } document
            || !_editor.GutterOptions.HasFlag(GutterOptions.LineNumbers))
            return;

        // The line numbers are right-aligned in (digits + 1) columns; the last is always blank.
        var column = Math.Max(1, document.LineCount).ToString().Length;
        var background = gutter.GetAttributeForRole(VisualRole.Normal).Background;
        foreach (var (row, lineNumber) in _lineByRow)
        {
            if (row >= gutter.Viewport.Height || !_changes.TryGetValue(lineNumber, out var kind))
                continue;
            var (glyph, hue) = kind switch
            {
                LineChangeKind.Added => ("▎", AddedHue),
                LineChangeKind.Modified => ("▎", ModifiedHue),
                LineChangeKind.RemovedAbove => ("▔", RemovedHue),
                _ => ("▁", RemovedHue),
            };
            gutter.SetAttribute(new GuiAttribute(ThemeSwitcher.Readable(hue, background), background));
            gutter.Move(column, row);
            gutter.AddStr(glyph);
        }
    }
}
