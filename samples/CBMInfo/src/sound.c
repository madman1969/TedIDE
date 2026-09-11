#include <sound.h>

const char *sound_chip_name(void)
{
#if defined(__C64__) || defined(__C128__) || defined(__CBM510__) || defined(__CBM610__)
    return "MOS 6581/8580 SID";
#elif defined(__VIC20__)
    /* The VIC-20's own video chip (see _vic.h) doubles as its sound
     * hardware - there's no separate sound chip to name. */
    return "VIC (integrated sound generators)";
#elif defined(__PLUS4__) || defined(__C16__)
    return "MOS 7360/8360 TED (integrated sound)";
#elif defined(__PET__)
    /* No sound hardware at all on a stock PET. */
    return "None";
#else
    return "Unknown";
#endif
}

unsigned sound_voice_count(void)
{
#if defined(__C64__) || defined(__C128__) || defined(__CBM510__) || defined(__CBM610__)
    return 3; /* SID: three independent oscillator/voice pairs - see _sid.h */
#elif defined(__VIC20__)
    return 4; /* VIC: three square-wave tone generators plus one noise
                 generator, sharing a single volume register - see _vic.h */
#elif defined(__PLUS4__) || defined(__C16__)
    return 2; /* TED: two independent sound channels - see _ted.h */
#else
    return 0;
#endif
}
