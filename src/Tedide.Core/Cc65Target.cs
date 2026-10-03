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
    /// The Commodore 8-bit machines cc65 can target - excludes non-Commodore platforms like
    /// Apple2*, Atari*, Nes, Atmos and Lynx. Used to restrict the Project Settings dialog's
    /// target dropdown to Commodore hardware.
    /// </summary>
    public static readonly Cc65Target[] CommodoreTargets =
    [
        Cc65Target.C64,
        Cc65Target.C128,
        Cc65Target.C16,
        Cc65Target.Plus4,
        Cc65Target.Vic20,
        Cc65Target.Pet,
        Cc65Target.Cbm510,
        Cc65Target.Cbm610,
        Cc65Target.Geos_Cbm,
    ];

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

    /// <summary>
    /// The CPU passed to cl65 as --cpu for this target: the processor the machine actually has,
    /// matching cc65's own per-target default (checked against a real cc65 2.19 by the .setcpu
    /// line it writes for each target), but passed explicitly so the build never depends on that
    /// default. Every Commodore machine's CPU - PET/VIC-20 6502, C64 6510, C128 8502, C16/Plus4
    /// 7501/8501, CBM-II 6509 - runs the plain NMOS 6502 instruction set, so they're all "6502".
    /// <paramref name="superCpu"/> switches a C64 to "65816", the SuperCPU cartridge's processor
    /// (cc65 then emits 65C02-level code such as STZ/BRA, which the 65816 runs; it never emits
    /// native 65816 code). It's ignored for every other target, same as
    /// <see cref="TedideProject.EnableSuperCpu"/> itself.
    /// </summary>
    public static string Cc65Cpu(this Cc65Target target, bool superCpu = false) => target switch
    {
        Cc65Target.C64 => superCpu ? "65816" : "6502",
        Cc65Target.C128 => "6502",
        Cc65Target.C16 => "6502",
        Cc65Target.Plus4 => "6502",
        Cc65Target.Vic20 => "6502",
        Cc65Target.Pet => "6502",
        Cc65Target.Apple2 => "6502",
        Cc65Target.Apple2Enh => "65c02",
        Cc65Target.Atari => "6502",
        Cc65Target.Atari5200 => "6502",
        Cc65Target.Nes => "6502",
        Cc65Target.Atmos => "6502",
        Cc65Target.Cbm510 => "6502",
        Cc65Target.Cbm610 => "6502",
        Cc65Target.Geos_Cbm => "6502",
        Cc65Target.Lynx => "65sc02",
        Cc65Target.None => "6502",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// <summary>
    /// The macros cc65 predefines for this target, from cc65 2.19's own documentation ("Predefined
    /// macros" in cc65.html). Code navigation evaluates <c>#if defined(...)</c> with these, so that a
    /// C64 project's COLOR_BLACK leads to c64.h - the header cbm.h actually includes for it - and
    /// not to the twenty other targets' headers that define it too. Note the c16 macro is defined
    /// for the Plus/4 as well, and the apple2 one for the enhanced Apple //e.
    /// </summary>
    public static IReadOnlyList<string> PredefinedMacros(this Cc65Target target) => target switch
    {
        Cc65Target.C64 => ["__CC65__", "__CBM__", "__C64__"],
        Cc65Target.C128 => ["__CC65__", "__CBM__", "__C128__"],
        Cc65Target.C16 => ["__CC65__", "__CBM__", "__C16__"],
        Cc65Target.Plus4 => ["__CC65__", "__CBM__", "__C16__", "__PLUS4__"],
        Cc65Target.Vic20 => ["__CC65__", "__CBM__", "__VIC20__"],
        Cc65Target.Pet => ["__CC65__", "__CBM__", "__PET__"],
        Cc65Target.Cbm510 => ["__CC65__", "__CBM__", "__CBM510__"],
        Cc65Target.Cbm610 => ["__CC65__", "__CBM__", "__CBM610__"],
        Cc65Target.Apple2 => ["__CC65__", "__APPLE2__"],
        Cc65Target.Apple2Enh => ["__CC65__", "__APPLE2__", "__APPLE2ENH__"],
        Cc65Target.Atari => ["__CC65__", "__ATARI__"],
        Cc65Target.Atari5200 => ["__CC65__", "__ATARI5200__"],
        Cc65Target.Nes => ["__CC65__", "__NES__"],
        Cc65Target.Atmos => ["__CC65__", "__ATMOS__"],
        Cc65Target.Geos_Cbm => ["__CC65__", "__GEOS__", "__GEOS_CBM__"],
        Cc65Target.Lynx => ["__CC65__", "__LYNX__"],
        Cc65Target.None => ["__CC65__"],
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// <summary>
    /// The CPU passed to opt6502 as -cpu for this target - see <see cref="Cc65Cpu"/>. opt6502 has
    /// no 65SC02 setting, and its 65C02 rewrites (STZ) are valid on the 65SC02, 65C02 and 65816
    /// alike, so anything with the CMOS instructions maps to its "65c02"/"65816" and the rest to
    /// "6502".
    /// </summary>
    public static string Opt6502Cpu(this Cc65Target target, bool superCpu = false) => target.Cc65Cpu(superCpu) switch
    {
        "65816" => "65816",
        "65c02" or "65sc02" => "65c02",
        _ => "6502",
    };

    /// <summary>
    /// Parses a cl65 target identifier (e.g. "c64", "apple2enh") case-insensitively, as accepted
    /// after -t/--target - the inverse of <see cref="ToCl65Id"/>.
    /// </summary>
    public static bool TryParse(string text, out Cc65Target target)
    {
        foreach (var candidate in Enum.GetValues<Cc65Target>())
        {
            if (string.Equals(candidate.ToCl65Id(), text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                target = candidate;
                return true;
            }
        }

        target = default;
        return false;
    }
}
