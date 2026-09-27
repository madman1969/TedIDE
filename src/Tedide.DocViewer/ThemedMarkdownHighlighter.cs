using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using GuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace Tedide.DocViewer;

/// <summary>
/// The Doc Viewer's Markdown highlighter: TextMate (VS Code grammars) still colors the tokens
/// inside fenced code blocks, but everything else - body text, headings, links, list markers,
/// quotes - is styled from the active app theme's own "Base" scheme instead of the TextMate
/// theme. A bare <see cref="TextMateSyntaxHighlighter"/> answers
/// <see cref="ISyntaxHighlighter.GetAttributeForScope"/> from VS Code's Light+/Dark+, so every
/// app theme got the same dark-red (light) or orange (dark) links and headings regardless of its
/// own palette. Colors are read from <see cref="SchemeManager"/> on every call, so a theme switch
/// applies on the next draw.
/// </summary>
internal sealed class ThemedMarkdownHighlighter : ISyntaxHighlighter
{
    private readonly TextMateSyntaxHighlighter _codeHighlighter = new();

    /// <summary>Picks the light or dark TextMate token colors for code blocks to suit
    /// <paramref name="background"/>.</summary>
    public void MatchBackground(Color background) =>
        _codeHighlighter.SetTheme(TextMateSyntaxHighlighter.GetThemeForBackground(background));

    public IReadOnlyList<StyledSegment> Highlight(string code, string? language) => _codeHighlighter.Highlight(code, language);

    public void ResetState() => _codeHighlighter.ResetState();

    public string ThemeName => _codeHighlighter.ThemeName.ToString();

    /// <summary>None - code blocks keep the view's own (theme-derived) dimmed background rather
    /// than the TextMate theme's white/black editor background.</summary>
    public Color? DefaultBackground => null;

    public GuiAttribute? GetAttributeForScope(MarkdownStyleRole role) => AttributeFor(role, SchemeManager.GetScheme("Base"));

    /// <summary>The theme attribute for <paramref name="role"/>, or null for the view's default
    /// scheme-based styling (plain text, emphasis, code, tables...).</summary>
    internal static GuiAttribute? AttributeFor(MarkdownStyleRole role, Scheme scheme) => role switch
    {
        MarkdownStyleRole.Heading or MarkdownStyleRole.HeadingMarker => WithStyle(scheme.CodeType, TextStyle.Bold),
        // Page links here are relative ("ca65.html"), which the view doesn't underline on its own.
        MarkdownStyleRole.Link => WithStyle(scheme.HotNormal, TextStyle.Underline),
        MarkdownStyleRole.ListMarker => WithStyle(scheme.HotNormal, TextStyle.Bold),
        MarkdownStyleRole.Quote or MarkdownStyleRole.ThematicBreak => scheme.CodeComment,
        _ => null,
    };

    private static GuiAttribute WithStyle(GuiAttribute attribute, TextStyle style) =>
        new(attribute.Foreground, attribute.Background, attribute.Style | style);
}
