using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using GuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace Tedide.App.Theming;

/// <summary>
/// Builds and registers the five named <see cref="Scheme"/> slots Terminal.Gui resolves views
/// against ("Base" for general content, "Menu" for the menu/status bars, "Dialog", "Accent" and
/// "Error"), and switches between them at runtime.
///
/// Views resolve their effective scheme by name from <see cref="SchemeManager"/> at draw time
/// (not once at construction), so overwriting these five entries and forcing a redraw restyles
/// every view in the app immediately - no need to touch individual views.
///
/// Note: built-in C/C++ syntax highlighting (Terminal.Gui.Editor's bundled "C++" definition) uses
/// literal colors from its .xshd data for token foreground (keywords, strings, comments, etc.),
/// not these schemes' Code* roles - so switching themes changes the editor's background and all
/// surrounding chrome, but not individual syntax-token hues.
/// </summary>
public static class ThemeSwitcher
{
    public static AppTheme Current { get; private set; } = AppTheme.Vs2026Dark;

    /// <summary>Raised after a theme has been applied and the app redrawn.</summary>
    public static event Action? Changed;

    public static void Apply(AppTheme theme)
    {
        var palette = theme switch
        {
            AppTheme.Vs2026Dark => Vs2026Dark(),
            AppTheme.Vs2026Light => Vs2026Light(),
            AppTheme.BorlandTurboC => BorlandTurboC(),
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, null),
        };

        SchemeManager.AddScheme("Base", palette.Base);
        SchemeManager.AddScheme("Menu", palette.Menu);
        SchemeManager.AddScheme("Dialog", palette.Dialog);
        SchemeManager.AddScheme("Accent", palette.Accent);
        SchemeManager.AddScheme("Error", palette.Error);

        Current = theme;
        Application.LayoutAndDraw(true);
        Changed?.Invoke();
    }

    private readonly record struct Palette(Scheme Base, Scheme Menu, Scheme Dialog, Scheme Accent, Scheme Error);

    /// <summary>
    /// Builds a full Scheme from just the handful of colors that actually vary between slots:
    /// the normal (unfocused) look, the focus/selection look, an accent color for hot keys and
    /// keyword-ish code roles, and a dimmed color for disabled/comment-ish roles.
    /// </summary>
    private static Scheme BuildScheme(GuiAttribute normal, GuiAttribute focus, GuiAttribute hot, GuiAttribute disabled)
    {
        var hotNormal = new GuiAttribute(hot.Foreground, normal.Background, hot.Style);
        var hotFocus = new GuiAttribute(hot.Foreground, focus.Background, hot.Style);

        return new Scheme(normal)
        {
            Normal = normal,
            HotNormal = hotNormal,
            Focus = focus,
            HotFocus = hotFocus,
            Active = focus,
            HotActive = hotFocus,
            Highlight = hotFocus,
            Editable = normal,
            ReadOnly = disabled,
            Disabled = disabled,
            Code = normal,
            CodeComment = disabled,
            CodeKeyword = hotNormal,
            CodeString = normal,
            CodeNumber = normal,
            CodeOperator = normal,
            CodeType = hotNormal,
            CodePreprocessor = disabled,
            CodeIdentifier = normal,
            CodeConstant = normal,
            CodePunctuation = normal,
            CodeFunctionName = hotNormal,
            CodeAttribute = normal,
        };
    }

    private static Palette Vs2026Dark()
    {
        Color editorBg = new(30, 30, 30);
        Color editorFg = new(212, 212, 212);
        Color selectionBg = new(38, 79, 120);
        Color accent = new(86, 156, 214);
        Color dimmed = new(106, 106, 106);

        Color chromeBg = new(51, 51, 51);
        Color chromeFg = new(241, 241, 241);
        Color chromeAccentBg = new(0, 122, 204);

        Color dialogBg = new(45, 45, 48);

        Color errorBg = new(80, 20, 20);
        Color errorFg = new(244, 71, 71);

        return new Palette(
            Base: BuildScheme(
                normal: new GuiAttribute(editorFg, editorBg),
                focus: new GuiAttribute(Color.White, selectionBg),
                hot: new GuiAttribute(accent, editorBg),
                disabled: new GuiAttribute(dimmed, editorBg)),
            Menu: BuildScheme(
                normal: new GuiAttribute(chromeFg, chromeBg),
                focus: new GuiAttribute(Color.White, chromeAccentBg),
                hot: new GuiAttribute(accent, chromeBg),
                disabled: new GuiAttribute(dimmed, chromeBg)),
            Dialog: BuildScheme(
                normal: new GuiAttribute(chromeFg, dialogBg),
                focus: new GuiAttribute(Color.White, chromeAccentBg),
                hot: new GuiAttribute(accent, dialogBg),
                disabled: new GuiAttribute(dimmed, dialogBg)),
            Accent: BuildScheme(
                normal: new GuiAttribute(Color.White, chromeAccentBg),
                focus: new GuiAttribute(Color.White, selectionBg),
                hot: new GuiAttribute(Color.White, chromeAccentBg),
                disabled: new GuiAttribute(dimmed, chromeAccentBg)),
            Error: BuildScheme(
                normal: new GuiAttribute(errorFg, errorBg),
                focus: new GuiAttribute(Color.White, errorFg),
                hot: new GuiAttribute(Color.White, errorBg),
                disabled: new GuiAttribute(dimmed, errorBg)));
    }

    private static Palette Vs2026Light()
    {
        Color editorBg = Color.White;
        Color editorFg = new(30, 30, 30);
        Color selectionBg = new(173, 214, 255);
        Color accent = new(0, 90, 158);
        Color dimmed = new(160, 160, 160);

        Color chromeBg = new(243, 243, 243);
        Color chromeAccentBg = new(0, 122, 204);

        Color dialogBg = Color.White;

        Color errorBg = new(253, 231, 233);
        Color errorFg = new(196, 25, 37);

        return new Palette(
            Base: BuildScheme(
                normal: new GuiAttribute(editorFg, editorBg),
                focus: new GuiAttribute(Color.Black, selectionBg),
                hot: new GuiAttribute(accent, editorBg),
                disabled: new GuiAttribute(dimmed, editorBg)),
            Menu: BuildScheme(
                normal: new GuiAttribute(editorFg, chromeBg),
                focus: new GuiAttribute(Color.White, chromeAccentBg),
                hot: new GuiAttribute(accent, chromeBg),
                disabled: new GuiAttribute(dimmed, chromeBg)),
            Dialog: BuildScheme(
                normal: new GuiAttribute(editorFg, dialogBg),
                focus: new GuiAttribute(Color.White, chromeAccentBg),
                hot: new GuiAttribute(accent, dialogBg),
                disabled: new GuiAttribute(dimmed, dialogBg)),
            Accent: BuildScheme(
                normal: new GuiAttribute(Color.White, chromeAccentBg),
                focus: new GuiAttribute(Color.Black, selectionBg),
                hot: new GuiAttribute(Color.White, chromeAccentBg),
                disabled: new GuiAttribute(dimmed, chromeAccentBg)),
            Error: BuildScheme(
                normal: new GuiAttribute(errorFg, errorBg),
                focus: new GuiAttribute(Color.White, errorFg),
                hot: new GuiAttribute(errorFg, errorBg),
                disabled: new GuiAttribute(dimmed, errorBg)));
    }

    /// <summary>
    /// The classic Borland Turbo C 2.0 look: a navy-blue editing area with white/yellow text,
    /// and a light gray (silver) menu/dialog chrome - built from the 16-color ANSI palette
    /// rather than true color, for an authentic retro terminal feel.
    /// </summary>
    private static Palette BorlandTurboC()
    {
        var editorNormal = new GuiAttribute(ColorName16.White, ColorName16.Blue);
        var editorFocus = new GuiAttribute(ColorName16.Black, ColorName16.Gray);
        var editorHot = new GuiAttribute(ColorName16.BrightYellow, ColorName16.Blue);
        var editorDisabled = new GuiAttribute(ColorName16.DarkGray, ColorName16.Blue);

        var chromeNormal = new GuiAttribute(ColorName16.Black, ColorName16.Gray);
        var chromeFocus = new GuiAttribute(ColorName16.Black, ColorName16.BrightCyan);
        var chromeHot = new GuiAttribute(ColorName16.Red, ColorName16.Gray);
        var chromeDisabled = new GuiAttribute(ColorName16.DarkGray, ColorName16.Gray);

        var accentNormal = new GuiAttribute(ColorName16.BrightYellow, ColorName16.Blue);
        var accentFocus = new GuiAttribute(ColorName16.Black, ColorName16.BrightCyan);

        var errorNormal = new GuiAttribute(ColorName16.White, ColorName16.Red);
        var errorFocus = new GuiAttribute(ColorName16.Red, ColorName16.White);

        return new Palette(
            Base: BuildScheme(editorNormal, editorFocus, editorHot, editorDisabled),
            Menu: BuildScheme(chromeNormal, chromeFocus, chromeHot, chromeDisabled),
            Dialog: BuildScheme(chromeNormal, chromeFocus, chromeHot, chromeDisabled),
            Accent: BuildScheme(accentNormal, accentFocus, chromeHot, editorDisabled),
            Error: BuildScheme(errorNormal, errorFocus, errorNormal, errorNormal));
    }
}
