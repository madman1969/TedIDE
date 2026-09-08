using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using GuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace Tedide.Theming;

/// <summary>
/// Builds and registers the six named <see cref="Scheme"/> slots Terminal.Gui resolves views
/// against ("Base" for general content, "Menu" for the menu/status bars, "Dialog", "Accent",
/// "Error" and "Warning"), and switches between them at runtime.
///
/// Views resolve their effective scheme by name from <see cref="SchemeManager"/> at draw time
/// (not once at construction), so overwriting these five entries and forcing a redraw restyles
/// every view in the app immediately - no need to touch individual views.
///
/// Note: built-in C/C++ syntax highlighting (Terminal.Gui.Editor's bundled "C++" definition) uses
/// literal colors from its .xshd data for most token foregrounds (keywords, strings, comments,
/// etc.), not these schemes' Code* roles - so switching themes changes the editor's background
/// and all surrounding chrome, but not most syntax-token hues. The exception is any Code* role a
/// theme sets explicitly here (e.g. Monokai's CodeNumber below): Terminal.Gui.Editor bridges xshd
/// color names to VisualRoles (see its XshdRoleMap - "Digits" maps to VisualRole.CodeNumber) and
/// prefers an explicit scheme Attribute for that role over the xshd's own literal color.
///
/// Every call to <see cref="Apply"/> persists the chosen theme via <see cref="ThemeSettings"/>, so
/// the app can restore it on the next run (see Program.cs, which applies <see cref="ThemeSettings.Load"/>
/// on startup instead of a hardcoded default).
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
            AppTheme.Monokai => Monokai(),
            AppTheme.Dracula => Dracula(),
            AppTheme.SolarizedDark => SolarizedDark(),
            AppTheme.SolarizedLight => SolarizedLight(),
            AppTheme.Commodore64 => Commodore64(),
            AppTheme.AmberPhosphor => AmberPhosphor(),
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, null),
        };

        SchemeManager.AddScheme("Base", palette.Base);
        SchemeManager.AddScheme("Menu", palette.Menu);
        SchemeManager.AddScheme("Dialog", palette.Dialog);
        SchemeManager.AddScheme("Accent", palette.Accent);
        SchemeManager.AddScheme("Error", palette.Error);
        SchemeManager.AddScheme("Warning", palette.Warning);

        Current = theme;
        Application.LayoutAndDraw(true);
        new ThemeSettings { Theme = theme }.Save();
        Changed?.Invoke();
    }

    private readonly record struct Palette(Scheme Base, Scheme Menu, Scheme Dialog, Scheme Accent, Scheme Error, Scheme Warning);

    /// <summary>
    /// Builds a full Scheme from just the handful of colors that actually vary between slots:
    /// the normal (unfocused) look, the focus/selection look, an accent color for hot keys and
    /// keyword-ish code roles, and a dimmed color for disabled/comment-ish roles.
    /// </summary>
    private static Scheme BuildScheme(GuiAttribute normal, GuiAttribute focus, GuiAttribute hot, GuiAttribute disabled, GuiAttribute? codeNumber = null)
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
            // Numeric literals ("Digits" in the bundled C/C++ highlighting, mapped through
            // Terminal.Gui.Editor's XshdRoleMap to VisualRole.CodeNumber) default to plain text
            // color like every other unstyled Code* role, but individual themes can override this
            // to something more distinctive - see Monokai() for the one theme that currently does.
            CodeNumber = codeNumber ?? normal,
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

        Color warningBg = new(80, 66, 20);
        Color warningFg = new(255, 204, 84);

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
                disabled: new GuiAttribute(dimmed, errorBg)),
            Warning: BuildScheme(
                normal: new GuiAttribute(warningFg, warningBg),
                focus: new GuiAttribute(Color.Black, warningFg),
                hot: new GuiAttribute(Color.White, warningBg),
                disabled: new GuiAttribute(dimmed, warningBg)));
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

        Color warningBg = new(255, 244, 206);
        Color warningFg = new(156, 110, 3);

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
                disabled: new GuiAttribute(dimmed, errorBg)),
            Warning: BuildScheme(
                normal: new GuiAttribute(warningFg, warningBg),
                focus: new GuiAttribute(Color.White, warningFg),
                hot: new GuiAttribute(warningFg, warningBg),
                disabled: new GuiAttribute(dimmed, warningBg)));
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

        var warningNormal = new GuiAttribute(ColorName16.Black, ColorName16.BrightYellow);
        var warningFocus = new GuiAttribute(ColorName16.BrightYellow, ColorName16.Black);

        return new Palette(
            Base: BuildScheme(editorNormal, editorFocus, editorHot, editorDisabled),
            Menu: BuildScheme(chromeNormal, chromeFocus, chromeHot, chromeDisabled),
            Dialog: BuildScheme(chromeNormal, chromeFocus, chromeHot, chromeDisabled),
            Accent: BuildScheme(accentNormal, accentFocus, chromeHot, editorDisabled),
            Error: BuildScheme(errorNormal, errorFocus, errorNormal, errorNormal),
            Warning: BuildScheme(warningNormal, warningFocus, warningNormal, warningNormal));
    }

    /// <summary>The classic Sublime Text/TextMate dark theme, built from its well-known palette.</summary>
    private static Palette Monokai()
    {
        Color editorBg = new(39, 40, 34);
        Color editorFg = new(248, 248, 242);
        Color selectionBg = new(73, 72, 62);
        Color accent = new(249, 38, 114);
        Color dimmed = new(117, 113, 94);

        Color chromeBg = new(62, 61, 50);
        Color chromeFg = editorFg;
        Color chromeAccentBg = new(174, 129, 255);

        Color dialogBg = editorBg;

        Color errorBg = new(75, 20, 30);
        Color errorFg = accent;

        Color warningBg = new(70, 62, 20);
        Color warningFg = new(230, 219, 116);

        // Monokai's own signature cyan-blue accent (the color Monokai itself uses for classes/
        // constants in its canonical Sublime Text palette) - paler than a plain saturated blue,
        // so numeric literals read clearly against the theme's dark background without clashing.
        Color numberFg = new(102, 217, 239);

        return new Palette(
            Base: BuildScheme(
                normal: new GuiAttribute(editorFg, editorBg),
                focus: new GuiAttribute(Color.White, selectionBg),
                hot: new GuiAttribute(accent, editorBg),
                disabled: new GuiAttribute(dimmed, editorBg),
                codeNumber: new GuiAttribute(numberFg, editorBg)),
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
                disabled: new GuiAttribute(dimmed, errorBg)),
            Warning: BuildScheme(
                normal: new GuiAttribute(warningFg, warningBg),
                focus: new GuiAttribute(Color.White, warningFg),
                hot: new GuiAttribute(Color.White, warningBg),
                disabled: new GuiAttribute(dimmed, warningBg)));
    }

    /// <summary>The Dracula theme (draculatheme.com) - a dark palette built around its signature purple.</summary>
    private static Palette Dracula()
    {
        Color editorBg = new(40, 42, 54);
        Color editorFg = new(248, 248, 242);
        Color selectionBg = new(68, 71, 90);
        Color accent = new(189, 147, 249);
        Color dimmed = new(98, 114, 164);

        Color chromeBg = new(33, 34, 44);
        Color chromeFg = editorFg;
        Color chromeAccentBg = accent;

        Color dialogBg = editorBg;

        Color errorBg = new(68, 20, 20);
        Color errorFg = new(255, 85, 85);

        Color warningBg = new(64, 58, 20);
        Color warningFg = new(241, 250, 140);

        // Dracula's own signature pale cyan-blue (the color Dracula itself uses for classes/types
        // in its canonical palette, draculatheme.com) - paler than a plain saturated blue, so
        // numeric literals read clearly against the theme's dark background without clashing.
        Color numberFg = new(139, 233, 253);

        return new Palette(
            Base: BuildScheme(
                normal: new GuiAttribute(editorFg, editorBg),
                focus: new GuiAttribute(Color.White, selectionBg),
                hot: new GuiAttribute(accent, editorBg),
                disabled: new GuiAttribute(dimmed, editorBg),
                codeNumber: new GuiAttribute(numberFg, editorBg)),
            Menu: BuildScheme(
                normal: new GuiAttribute(chromeFg, chromeBg),
                focus: new GuiAttribute(editorBg, chromeAccentBg),
                hot: new GuiAttribute(accent, chromeBg),
                disabled: new GuiAttribute(dimmed, chromeBg)),
            Dialog: BuildScheme(
                normal: new GuiAttribute(chromeFg, dialogBg),
                focus: new GuiAttribute(editorBg, chromeAccentBg),
                hot: new GuiAttribute(accent, dialogBg),
                disabled: new GuiAttribute(dimmed, dialogBg)),
            Accent: BuildScheme(
                normal: new GuiAttribute(editorBg, chromeAccentBg),
                focus: new GuiAttribute(Color.White, selectionBg),
                hot: new GuiAttribute(editorBg, chromeAccentBg),
                disabled: new GuiAttribute(dimmed, chromeAccentBg)),
            Error: BuildScheme(
                normal: new GuiAttribute(errorFg, errorBg),
                focus: new GuiAttribute(Color.White, errorFg),
                hot: new GuiAttribute(Color.White, errorBg),
                disabled: new GuiAttribute(dimmed, errorBg)),
            Warning: BuildScheme(
                normal: new GuiAttribute(warningFg, warningBg),
                focus: new GuiAttribute(editorBg, warningFg),
                hot: new GuiAttribute(Color.White, warningBg),
                disabled: new GuiAttribute(dimmed, warningBg)));
    }

    /// <summary>Ethan Schoonover's Solarized (dark variant) - a low-contrast, accessibility-minded palette.</summary>
    private static Palette SolarizedDark()
    {
        Color editorBg = new(0, 43, 54);
        Color editorFg = new(131, 148, 150);
        Color selectionBg = new(7, 54, 66);
        Color accent = new(38, 139, 210);
        Color dimmed = new(88, 110, 117);

        Color chromeBg = selectionBg;
        Color chromeFg = editorFg;
        Color chromeAccentBg = accent;

        Color dialogBg = editorBg;

        Color errorBg = new(56, 15, 15);
        Color errorFg = new(220, 50, 47);

        Color warningBg = new(48, 40, 10);
        Color warningFg = new(203, 161, 45);

        // A paler tint of Solarized's own blue accent (#268bd2) - keeps numeric literals in the
        // same hue family as the theme's keyword/hot color, just lighter, so they read distinctly
        // against the very dark editor background without introducing an unrelated color.
        Color numberFg = new(108, 182, 224);

        return new Palette(
            Base: BuildScheme(
                normal: new GuiAttribute(editorFg, editorBg),
                focus: new GuiAttribute(Color.White, chromeAccentBg),
                hot: new GuiAttribute(accent, editorBg),
                disabled: new GuiAttribute(dimmed, editorBg),
                codeNumber: new GuiAttribute(numberFg, editorBg)),
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
                disabled: new GuiAttribute(dimmed, errorBg)),
            Warning: BuildScheme(
                normal: new GuiAttribute(warningFg, warningBg),
                focus: new GuiAttribute(Color.White, warningFg),
                hot: new GuiAttribute(Color.White, warningBg),
                disabled: new GuiAttribute(dimmed, warningBg)));
    }

    /// <summary>The light variant of Solarized, swapping its background/foreground tones.</summary>
    private static Palette SolarizedLight()
    {
        Color editorBg = new(253, 246, 227);
        Color editorFg = new(101, 123, 131);
        Color selectionBg = new(238, 232, 213);
        Color accent = new(38, 139, 210);
        Color dimmed = new(147, 161, 161);

        Color chromeBg = selectionBg;
        Color chromeAccentBg = accent;

        Color dialogBg = editorBg;

        Color errorBg = new(250, 220, 218);
        Color errorFg = new(220, 50, 47);

        Color warningBg = new(250, 240, 200);
        Color warningFg = new(150, 116, 8);

        return new Palette(
            Base: BuildScheme(
                normal: new GuiAttribute(editorFg, editorBg),
                focus: new GuiAttribute(Color.White, chromeAccentBg),
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
                disabled: new GuiAttribute(dimmed, errorBg)),
            Warning: BuildScheme(
                normal: new GuiAttribute(warningFg, warningBg),
                focus: new GuiAttribute(Color.White, warningFg),
                hot: new GuiAttribute(warningFg, warningBg),
                disabled: new GuiAttribute(dimmed, warningBg)));
    }

    /// <summary>
    /// The Commodore 64's default boot-screen look (Pepto palette) - blue background, light-blue
    /// text - a fitting default for an IDE that targets 6502 machines including the C64 itself.
    /// </summary>
    private static Palette Commodore64()
    {
        var editorNormal = new GuiAttribute(new Color(108, 94, 181), new Color(53, 40, 121));
        var editorFocus = new GuiAttribute(ColorName16.White, new Color(111, 61, 134));
        var editorHot = new GuiAttribute(ColorName16.White, new Color(53, 40, 121));
        var editorDisabled = new GuiAttribute(new Color(108, 108, 108), new Color(53, 40, 121));

        var chromeNormal = new GuiAttribute(ColorName16.White, new Color(111, 61, 134));
        var chromeFocus = new GuiAttribute(new Color(53, 40, 121), new Color(112, 164, 178));
        var chromeHot = new GuiAttribute(new Color(184, 199, 111), new Color(111, 61, 134));
        var chromeDisabled = new GuiAttribute(new Color(108, 108, 108), new Color(111, 61, 134));

        var accentNormal = new GuiAttribute(new Color(53, 40, 121), new Color(112, 164, 178));
        var accentFocus = new GuiAttribute(ColorName16.White, new Color(111, 61, 134));

        var errorNormal = new GuiAttribute(ColorName16.White, new Color(104, 55, 43));
        var errorFocus = new GuiAttribute(new Color(104, 55, 43), ColorName16.White);

        var warningNormal = new GuiAttribute(new Color(53, 40, 121), new Color(184, 199, 111));
        var warningFocus = new GuiAttribute(new Color(184, 199, 111), new Color(53, 40, 121));

        // C64 palette color 14, "light blue" - already used above for the chrome/accent
        // background - reused here as a paler blue than the lavender-purple editor foreground,
        // so numeric literals read distinctly against the dark blue-purple editor background.
        var editorNumber = new GuiAttribute(new Color(112, 164, 178), new Color(53, 40, 121));

        return new Palette(
            Base: BuildScheme(editorNormal, editorFocus, editorHot, editorDisabled, editorNumber),
            Menu: BuildScheme(chromeNormal, chromeFocus, chromeHot, chromeDisabled),
            Dialog: BuildScheme(chromeNormal, chromeFocus, chromeHot, chromeDisabled),
            Accent: BuildScheme(accentNormal, accentFocus, chromeHot, editorDisabled),
            Error: BuildScheme(errorNormal, errorFocus, errorNormal, errorNormal),
            Warning: BuildScheme(warningNormal, warningFocus, warningNormal, warningNormal));
    }

    /// <summary>
    /// A monochrome amber-phosphor terminal look, evoking early CRT terminals of the 6502 era.
    /// </summary>
    private static Palette AmberPhosphor()
    {
        Color background = Color.Black;
        Color amber = new(255, 176, 0);
        Color brightAmber = new(255, 210, 90);
        Color dimAmber = new(140, 95, 0);
        Color selectionBg = new(80, 55, 0);

        var editorNormal = new GuiAttribute(amber, background);
        var editorFocus = new GuiAttribute(brightAmber, selectionBg);
        var editorHot = new GuiAttribute(brightAmber, background);
        var editorDisabled = new GuiAttribute(dimAmber, background);

        var chromeNormal = new GuiAttribute(amber, background);
        var chromeFocus = new GuiAttribute(background, amber);
        var chromeHot = new GuiAttribute(brightAmber, background);
        var chromeDisabled = new GuiAttribute(dimAmber, background);

        var accentNormal = new GuiAttribute(background, amber);

        var errorNormal = new GuiAttribute(Color.Black, new Color(200, 60, 30));
        var errorFocus = new GuiAttribute(new Color(200, 60, 30), Color.White);

        // Amber-on-black is already the theme's whole palette, so a warning needs to invert
        // (black-on-bright-amber) to read as distinct from both normal text and the red error
        // scheme, rather than trying to find a second hue within a deliberately monochrome theme.
        var warningNormal = new GuiAttribute(Color.Black, brightAmber);
        var warningFocus = new GuiAttribute(brightAmber, Color.Black);

        // Amber-on-black is already the theme's whole palette (see the Warning scheme's comment
        // above), so numeric literals stand out via brightness, not an introduced hue - the same
        // brightAmber used for keywords/hot text, not a new color.
        var editorNumber = new GuiAttribute(brightAmber, background);

        return new Palette(
            Base: BuildScheme(editorNormal, editorFocus, editorHot, editorDisabled, editorNumber),
            Menu: BuildScheme(chromeNormal, chromeFocus, chromeHot, chromeDisabled),
            Dialog: BuildScheme(chromeNormal, chromeFocus, chromeHot, chromeDisabled),
            Accent: BuildScheme(accentNormal, chromeFocus, chromeHot, editorDisabled),
            Error: BuildScheme(errorNormal, errorFocus, errorNormal, errorNormal),
            Warning: BuildScheme(warningNormal, warningFocus, warningNormal, warningNormal));
    }
}
