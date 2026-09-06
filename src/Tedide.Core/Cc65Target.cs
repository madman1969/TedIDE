namespace Tedide.Core;

/// <summary>
/// A cc65 target platform, as passed to cl65 via -t/--target.
/// </summary>
public enum Cc65Target
{
    C64,
    C128,
    C16,
    Plus4,
    Vic20,
    Pet,
    Apple2,
    Apple2Enh,
    Atari,
    Atari5200,
    Nes,
    Atmos,
    Cbm510,
    Cbm610,
    Geos_Cbm,
    Lynx,
    None,
}

public static class Cc65TargetExtensions
{
    /// <summary>
    /// The identifier cl65 expects after -t, e.g. "c64", "apple2enh".
    /// </summary>
    public static string ToCl65Id(this Cc65Target target) => target switch
    {
        Cc65Target.C64 => "c64",
        Cc65Target.C128 => "c128",
        Cc65Target.C16 => "c16",
        Cc65Target.Plus4 => "plus4",
        Cc65Target.Vic20 => "vic20",
        Cc65Target.Pet => "pet",
        Cc65Target.Apple2 => "apple2",
        Cc65Target.Apple2Enh => "apple2enh",
        Cc65Target.Atari => "atari",
        Cc65Target.Atari5200 => "atari5200",
        Cc65Target.Nes => "nes",
        Cc65Target.Atmos => "atmos",
        Cc65Target.Cbm510 => "cbm510",
        Cc65Target.Cbm610 => "cbm610",
        Cc65Target.Geos_Cbm => "geos-cbm",
        Cc65Target.Lynx => "lynx",
        Cc65Target.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// <summary>
    /// The conventional output file extension for a built binary on this target.
    /// </summary>
    public static string DefaultOutputExtension(this Cc65Target target) => target switch
    {
        Cc65Target.C64 => ".prg",
        Cc65Target.C128 => ".prg",
        Cc65Target.C16 => ".prg",
        Cc65Target.Plus4 => ".prg",
        Cc65Target.Vic20 => ".prg",
        Cc65Target.Pet => ".prg",
        Cc65Target.Apple2 => ".bin",
        Cc65Target.Apple2Enh => ".bin",
        Cc65Target.Atari => ".xex",
        Cc65Target.Atari5200 => ".bin",
        Cc65Target.Nes => ".nes",
        Cc65Target.Atmos => ".bin",
        Cc65Target.Cbm510 => ".prg",
        Cc65Target.Cbm610 => ".prg",
        Cc65Target.Geos_Cbm => ".cvt",
        Cc65Target.Lynx => ".lnx",
        Cc65Target.None => ".bin",
        _ => ".bin",
    };
}
