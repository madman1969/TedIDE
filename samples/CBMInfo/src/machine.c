#include <machine.h>

#if defined(__C64__)
#include <c64.h>
#endif

const char *machine_model(void)
{
#if defined(__C64__)
    /* get_ostype() is only implemented for a handful of cc65 targets
     * (c64.h/apple2.h/atari.h/cx16.h - see the cc65 function reference) -
     * of the platforms this project supports, only the C64 build can go
     * beyond "it's a C64" and confirm exactly which ROM/hardware variant
     * it's actually running on. */
    switch (get_ostype())
    {
        case C64_OS_SX64:
            return "Commodore SX-64";
        case C64_OS_PET64:
            return "Commodore PET64 (colour PET)";
        case C64_DTV:
            return "Commodore 64 DTV";
        case C64_OS_US:
            return "Commodore 64 (NTSC/US ROM)";
        case C64_EU_NEW:
            return "Commodore 64 (PAL/EU ROM)";
        case C64_EU_OLD:
            return "Commodore 64 (PAL/EU ROM, old)";
        default:
            return "Commodore 64";
    }
#elif defined(__C128__)
    return "Commodore 128";
#elif defined(__VIC20__)
    return "Commodore VIC-20";
#elif defined(__PLUS4__)
    return "Commodore Plus/4";
#elif defined(__C16__)
    return "Commodore 16";
#elif defined(__PET__)
    return "Commodore PET";
#elif defined(__CBM510__)
    return "Commodore P500 / CBM 510";
#elif defined(__CBM610__)
    return "Commodore CBM 610/620 (B-series)";
#else
    return "Unknown Commodore machine";
#endif
}

unsigned machine_address_bits(void)
{
    return 16;
}

unsigned machine_word_bits(void)
{
    return 8;
}
