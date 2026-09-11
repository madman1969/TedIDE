#include <cpu.h>
#include <6502.h>

#if defined(__C128__)
#include <accelerator.h>
#endif

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

unsigned cpu_speed_khz(void)
{
#if defined(__C128__)
    /* Genuinely dynamic - the C128 switches between 1MHz (C64-compatible)
     * and 2MHz (native) mode under program control, and get_c128_speed()
     * reads back whichever mode is actually active right now rather than
     * assuming one or the other. */
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
