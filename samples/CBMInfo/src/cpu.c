#include <cpu.h>
#include <6502.h>

#if defined(__C64__) || defined(__C128__)
#include <accelerator.h>
#endif

/* Populated by cpu_detect_fast_mode() - see cpu.h's own comment on why
 * these three functions exist separately rather than folding straight into
 * cpu_speed_khz() (accelerator detection is a real hardware probe worth
 * doing exactly once, not on every call). */
static int         s_fast_mode_supported   = 0;
static const char *s_fast_mode_description = "Not checked yet";
static unsigned    s_fast_khz              = 0; /* 0 = fall back to the normal per-platform figure below */

const char *cpu_name(void)
{
    /* getcpu() distinguishes CPU *families* (see the CPU_xxx constants in
     * 6502.h), but every target this project supports reports CPU_6502 -
     * they're all opcode-compatible NMOS 6502 variants (6510/8502/7501/
     * 8501/6502), just different model numbers depending on which on-chip
     * I/O port the machine actually wires up, which getcpu() itself can't
     * tell apart. Confirm that's really what was detected before naming
     * the exact chip, rather than just assuming it. */
    if (getcpu() != CPU_6502)
    {
        return "Unknown 65xx-family CPU";
    }

#if defined(__C64__)
    return "MOS 6510";
#elif defined(__C128__)
    return "MOS 8502";
#elif defined(__VIC20__)
    return "MOS 6502";
#elif defined(__PLUS4__) || defined(__C16__)
    return "MOS 7501/8501";
#elif defined(__PET__)
    return "MOS 6502";
#elif defined(__CBM510__) || defined(__CBM610__)
    /* The CBM-II series (P500/510 and the 610/620 "B" series) used a 6509 -
     * a 6502 core with extra bank-switching address lines wired to a bank
     * register, not new opcodes - so it's still opcode-identical and still
     * reports CPU_6502 above. */
    return "MOS 6509";
#else
    return "MOS 6502-compatible";
#endif
}

void cpu_detect_fast_mode(void)
{
#if defined(__C128__)
    /* The C128's native 1/2 MHz switch is built into every C128 - not an
     * add-on to detect, so this is unconditionally "supported" here. */
    set_c128_speed(SPEED_FAST);
    s_fast_mode_supported   = 1;
    s_fast_mode_description = "C128 native speed switch (2 MHz)";
    s_fast_khz              = 2000;
#elif defined(__C64__)
    /* Checked in roughly fastest-first order - only one of these would
     * realistically be present at once, but detect_xxx() before set_xxx()
     * is what accelerator.h's own docs require for every one of them. */
    if (detect_scpu())
    {
        set_scpu_speed(SPEED_FAST);
        s_fast_mode_supported   = 1;
        s_fast_mode_description = "SuperCPU cartridge (20 MHz)";
        s_fast_khz              = 20000;
    }
    else if (detect_turbomaster())
    {
        set_turbomaster_speed(SPEED_FAST);
        s_fast_mode_supported   = 1;
        s_fast_mode_description = "Turbo Master cartridge (4 MHz)";
        s_fast_khz              = 4000;
    }
    else if (detect_c65())
    {
        set_c65_speed(SPEED_FAST);
        s_fast_mode_supported   = 1;
        s_fast_mode_description = "C65/C64DX in C64 mode (3.5 MHz)";
        s_fast_khz              = 3500;
    }
    else if (detect_chameleon())
    {
        set_chameleon_speed(SPEED_FAST);
        s_fast_mode_supported   = 1;
        s_fast_mode_description = "Chameleon cartridge (maximum speed)";
        /* Chameleon's maximum tier is configurable per-cartridge -
         * accelerator.h documents fixed MHz figures for its individual
         * SPEED_2X..SPEED_6X steps, but not for "maximum", so s_fast_khz
         * is deliberately left at 0 here rather than inventing a number -
         * cpu_speed_khz() falls back to the normal C64 nominal figure. */
    }
    else if (detect_c64dtv())
    {
        set_c64dtv_speed(SPEED_FAST);
        s_fast_mode_supported   = 1;
        s_fast_mode_description = "C64DTV (fast mode)";
        /* Same caveat as Chameleon above - accelerator.h never states an
         * absolute clock rate for the C64DTV's fast mode, only that it's
         * faster than SPEED_SLOW. */
    }
    else
    {
        s_fast_mode_supported   = 0;
        s_fast_mode_description = "Not available on this machine";
    }
#else
    s_fast_mode_supported   = 0;
    s_fast_mode_description = "Not available on this machine";
#endif
}

int cpu_supports_fast_mode(void)
{
    return s_fast_mode_supported;
}

const char *cpu_fast_mode_description(void)
{
    return s_fast_mode_description;
}

unsigned cpu_speed_khz(void)
{
    if (s_fast_khz != 0)
    {
        return s_fast_khz;
    }

#if defined(__C128__)
    /* No accelerator with a known exact rate was engaged above, but the
     * C128's own native switch is still genuinely dynamic on its own -
     * get_c128_speed() reads back whichever mode is actually active right
     * now rather than assuming one or the other. */
    return (get_c128_speed() == SPEED_SLOW) ? 1000 : 2000;
#elif defined(__C64__)
    return 1020;
#elif defined(__VIC20__)
    return 1100;
#elif defined(__PLUS4__) || defined(__C16__)
    return 1760;
#elif defined(__PET__)
    return 1000;
#elif defined(__CBM510__) || defined(__CBM610__)
    return 1000;
#else
    return 0;
#endif
}
