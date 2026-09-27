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
/// <see cref="Apply"/> persists the chosen theme via <see cref="ThemeSettings"/> (unless told not
/// to), so the app can restore it on the next run (see each Program.cs, which applies
/// <see cref="ThemeSettings.Load"/> on startup instead of a hardcoded default).
/// </summary>
public static class ThemeSwitcher
{
    public static AppTheme Current { get; private set; } = AppTheme.Vs2026Dark;

    /// <summary>Raised after a theme has been applied and the app redrawn.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Raised when the chosen theme couldn't be saved - the theme is still applied for this run,
    /// just not remembered for the next. Saving used to throw straight out of <see cref="Apply"/>,
    /// which is called from the Theme menu and at startup: confirmed live, a read-only
    /// settings.json stopped the Doc Viewer (and, through this same shared code, Tedide.App)
    /// from even starting. This library has no logger of its own, so each app reports it.
    /// </summary>
    public static event Action<Exception>? SaveFailed;

    /// <param name="persist">
    /// False at startup, when <paramref name="theme"/> was just read from the settings file -
    /// writing it straight back is pointless, and was the one save that could stop the app
    /// starting at all.
    /// </param>
    public static void Apply(AppTheme theme, bool persist = true)
    {
        var palette = PaletteFor(theme);

        SchemeManager.AddScheme("Base", palette.Base);
        SchemeManager.AddScheme("Menu", palette.Menu);
        SchemeManager.AddScheme("Dialog", palette.Dialog);
        SchemeManager.AddScheme("Accent", palette.Accent);
        SchemeManager.AddScheme("Error", palette.Error);
        SchemeManager.AddScheme("Warning", palette.Warning);

        Current = theme;
        Application.LayoutAndDraw(true);
        if (persist)
        {
            try
            {
                new ThemeSettings { Theme = theme }.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                SaveFailed?.Invoke(ex);
            }
        }
        Changed?.Invoke();
    }

    private readonly record struct Palette(Scheme Base, Scheme Menu, Scheme Dialog, Scheme Accent, Scheme Error, Scheme Warning);

    /// <summary>
    /// Every scheme <paramref name="theme"/> registers, by slot name - what <see cref="Apply"/>
    /// installs, exposed so tests can check the colors without a running app.
    /// </summary>
    private static Palette PaletteFor(AppTheme theme) => theme switch
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

    internal static IReadOnlyDictionary<string, Scheme> SchemesFor(AppTheme theme)
    {
        var palette = PaletteFor(theme);
        return new Dictionary<string, Scheme>
        {
            ["Base"] = palette.Base, ["Menu"] = palette.Menu, ["Dialog"] = palette.Dialog,
            ["Accent"] = palette.Accent, ["Error"] = palette.Error, ["Warning"] = palette.Warning,
        };
    }

    /// <summary>
    /// The attribute a hotkey letter is drawn with on text styled <paramref name="text"/>: the
    /// theme's hot color - unless that can't be told apart from the background or the surrounding
    /// text, in which case the letter keeps the text's own color and is underlined instead. Several
    /// themes use one accent color for both hotkeys and the focus highlight, which made a focused
    /// item's hotkey letter vanish into its own background (Solarized Light/Dark and Dracula menus,
    /// several themes' error/warning boxes - seen live as "ile" for File, "ookmarks", "emove").
    /// A hot color that differs in hue but is too close in brightness to read (Borland's bright red
    /// on gray, 1.01:1) has its lightness nudged to <see cref="MutedContrast"/> first; only if that
    /// can't be reached while staying distinct does it fall back to the underline.
    /// </summary>
    private static GuiAttribute HotkeyAttribute(GuiAttribute hot, GuiAttribute text)
    {
        var underlined = new GuiAttribute(text.Foreground, text.Background, text.Style | TextStyle.Underline);
        if (Indistinguishable(hot.Foreground, text.Background) || Indistinguishable(hot.Foreground, text.Foreground))
            return underlined;

        var (hotForeground, reached) = AdjustLightness(hot.Foreground, text.Background, MutedContrast);
        return reached && !Indistinguishable(hotForeground, text.Background) && !Indistinguishable(hotForeground, text.Foreground)
            ? new GuiAttribute(hotForeground, text.Background, hot.Style)
            : underlined;
    }

    /// <summary>WCAG AA contrast for body text - the level every Normal/Focus pair must reach.</summary>
    internal const double TextContrast = 4.5;

    /// <summary>WCAG's lower bar (large text / UI components), applied to what is meant to look
    /// muted or secondary: hotkey letters, disabled and read-only text, comments.</summary>
    internal const double MutedContrast = 3.0;

    /// <summary>
    /// <paramref name="attribute"/> with its text readable against its background (at least
    /// <paramref name="target"/>:1). Several themes' authored colors fell short - Commodore 64's
    /// light blue on blue text 2.3:1, Monokai's white on yellow warning highlight 1.4:1, the
    /// Solarized themes' white on blue selection 3.7:1. Rather than hand-tuning each color, the
    /// lightness of the text or of its background is nudged - whichever reaches the target with
    /// the smaller visible change - keeping the hue, so each theme keeps its look. Pairs that
    /// already pass are returned unchanged.
    /// </summary>
    private static GuiAttribute Readable(GuiAttribute attribute, double target)
    {
        var (foreground, background) = (attribute.Foreground, attribute.Background);
        if (ContrastRatio(foreground, background) >= target)
            return attribute;

        var textMoved = AdjustLightness(foreground, background, target);
        var backgroundMoved = AdjustLightness(background, foreground, target);
        (foreground, background) = (textMoved.Reached, backgroundMoved.Reached) switch
        {
            (true, true) => DeltaE(foreground, textMoved.Color) <= DeltaE(background, backgroundMoved.Color)
                ? (textMoved.Color, background)
                : (foreground, backgroundMoved.Color),
            (true, false) => (textMoved.Color, background),
            (false, true) => (foreground, backgroundMoved.Color),
            // Neither alone gets there: take the text as far as it goes, then the background.
            _ => (textMoved.Color, AdjustLightness(background, textMoved.Color, target).Color),
        };
        return new GuiAttribute(foreground, background, attribute.Style);
    }

    /// <summary><paramref name="attribute"/> with only its text color adjusted to reach
    /// <paramref name="target"/>:1 - for roles drawn on another role's background, which they
    /// mustn't change.</summary>
    private static GuiAttribute WithReadableForeground(GuiAttribute attribute, double target) =>
        new(AdjustLightness(attribute.Foreground, attribute.Background, target).Color, attribute.Background, attribute.Style);

    /// <summary>Moves <paramref name="attribute"/>'s background along with the Normal background it
    /// was authored against, if Readable changed that.</summary>
    private static GuiAttribute Follow(GuiAttribute attribute, Color authoredBackground, Color adjustedBackground) =>
        SameColor(attribute.Background, authoredBackground)
            ? new GuiAttribute(attribute.Foreground, adjustedBackground, attribute.Style)
            : attribute;

    private static bool SameColor(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;

    /// <summary>
    /// Lightens or darkens <paramref name="color"/> - away from <paramref name="against"/>'s own
    /// lightness, keeping its hue and chroma - one CIELAB L* step at a time until it reaches
    /// <paramref name="target"/>:1 contrast with it or runs out of room at white/black. Reached
    /// says which. Already-passing colors come back unchanged.
    /// </summary>
    private static (Color Color, bool Reached) AdjustLightness(Color color, Color against, double target)
    {
        if (ContrastRatio(color, against) >= target)
            return (color, true);

        var (l, a, b) = ToLab(color);
        var step = RelativeLuminance(color) >= RelativeLuminance(against) ? 1.0 : -1.0;
        var candidate = color;
        for (var lightness = l + step; lightness is >= 0 and <= 100; lightness += step)
        {
            candidate = FromLab(lightness, a, b);
            if (ContrastRatio(candidate, against) >= target)
                return (candidate, true);
        }
        return (candidate, false);
    }

    /// <summary>WCAG 2 contrast ratio between two colors, from 1:1 (identical brightness) to 21:1.</summary>
    internal static double ContrastRatio(Color a, Color b)
    {
        var (l1, l2) = (RelativeLuminance(a), RelativeLuminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double DeltaE(Color x, Color y)
    {
        var (l1, a1, b1) = ToLab(x);
        var (l2, a2, b2) = ToLab(y);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    /// <summary>CIELAB (D65) back to sRGB, clamped into gamut.</summary>
    private static Color FromLab(double l, double a, double b)
    {
        var fy = (l + 16) / 116;
        var (fx, fz) = (fy + a / 500, fy - b / 200);
        static double Inverse(double t) => t * t * t > 0.008856 ? t * t * t : (t - 16.0 / 116) / 7.787;
        var (x, y, z) = (Inverse(fx) * 0.95047, Inverse(fy), Inverse(fz) * 1.08883);
        static int Channel(double linear)
        {
            linear = Math.Clamp(linear, 0, 1);
            var s = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            return (int)Math.Round(Math.Clamp(s, 0, 1) * 255);
        }
        return new Color(
            Channel(3.2406 * x - 1.5372 * y - 0.4986 * z),
            Channel(-0.9689 * x + 1.8758 * y + 0.0415 * z),
            Channel(0.0557 * x - 0.2040 * y + 1.0570 * z));
    }

    /// <summary>
    /// Whether two colors are too alike to tell apart: a perceptual (CIELAB ΔE76) difference under
    /// 20. Perceptual, because neither brightness nor raw RGB distance tracks what the eye sees -
    /// Borland Turbo C's red hotkeys on gray are about equal in brightness yet plainly visible, and
    /// Solarized's accent blue on its gray text is close in RGB yet obviously a different color
    /// (ΔE about 38), while Amber Phosphor's light amber on amber really is hard to see (about 19).
    /// </summary>
    internal static bool Indistinguishable(Color a, Color b) => DeltaE(a, b) < 20;

    /// <summary>sRGB to CIELAB (D65 white point).</summary>
    private static (double L, double A, double B) ToLab(Color c)
    {
        static double Linear(int v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        var (r, g, b) = (Linear(c.R), Linear(c.G), Linear(c.B));
        var x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        var (fx, fy, fz) = (F(x), F(y), F(z));
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }


    /// <summary>
    /// Builds a full Scheme from just the handful of colors that actually vary between slots:
    /// the normal (unfocused) look, the focus/selection look, an accent color for hot keys and
    /// keyword-ish code roles, and a dimmed color for disabled/comment-ish roles.
    /// </summary>
    private static Scheme BuildScheme(GuiAttribute normal, GuiAttribute focus, GuiAttribute hot, GuiAttribute disabled, GuiAttribute? codeNumber = null)
    {
        // Readability first, so everything below is derived from the adjusted colors - see
        // Readable/WithReadableForeground. A theme whose colors already pass is left untouched.
        var authoredNormalBackground = normal.Background;
        normal = Readable(normal, TextContrast);
        focus = Readable(focus, TextContrast);
        disabled = WithReadableForeground(Follow(disabled, authoredNormalBackground, normal.Background), MutedContrast);
        if (codeNumber is { } number)
            codeNumber = WithReadableForeground(Follow(number, authoredNormalBackground, normal.Background), MutedContrast);

        var hotNormal = HotkeyAttribute(hot, normal);
        var hotFocus = HotkeyAttribute(hot, focus);

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
