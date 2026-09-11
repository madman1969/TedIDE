#include <video.h>
#include <conio.h>

/* Only pulled in for the VIC-II-based targets below, each of which defines
 * its own VIC macro (a struct __vic2* at that platform's own base address -
 * $D000 for C64/C128, $D800 for the CBM510) - including the *right* one of
 * these per platform is what makes the shared code in detect_video_system()
 * below work unmodified across all three. */
#if defined(__C64__)
#include <c64.h>
#elif defined(__C128__)
#include <c128.h>
#elif defined(__CBM510__)
#include <cbm510.h>
#elif defined(__PLUS4__) || defined(__C16__)
#include <cbm264.h>
#endif

VideoSystem detect_video_system(void)
{
#if defined(__C64__) || defined(__C128__) || defined(__CBM510__)
    /* VIC-II: VIC.rasterline holds raster line bits 0-7, and bit 7 of
     * VIC.ctrl1 holds bit 8 - reconstructed together into the true 0-311
     * (PAL) / 0-261 (NTSC) line number every iteration (not just
     * VIC.rasterline alone, which only ever counts 0-255 and would wrap the
     * same way on both standards). Tracking the highest full value seen
     * across many samples - rather than trusting a single snapshot, which
     * could land anywhere in the frame - is what actually tells the two
     * apart. */
    unsigned max_raster = 0;
    unsigned int i;

    for (i = 0; i < 1000; ++i)
    {
        unsigned char lo = VIC.rasterline;
        unsigned char hi8 = (VIC.ctrl1 & 0x80) ? 1 : 0;
        unsigned current = ((unsigned)hi8 << 8) | lo;

        if (current > max_raster)
        {
            max_raster = current;
        }
    }

    /* NTSC's highest line is 261, PAL's is 311 - 280 sits cleanly between
     * the two. */
    return (max_raster > 280) ? VIDEO_PAL : VIDEO_NTSC;
#elif defined(__PLUS4__) || defined(__C16__)
    /* TED exposes the same idea as the VIC-II above, but as two whole,
     * unshared registers instead of one register plus a borrowed bit of
     * another (see _ted.h) - same reconstruct-then-track-the-max approach,
     * applied to TED's own raster counter. */
    unsigned max_raster = 0;
    unsigned int i;

    for (i = 0; i < 1000; ++i)
    {
        unsigned current = ((unsigned)(TED.rasterline_hi & 0x01) << 8) | TED.rasterline_lo;

        if (current > max_raster)
        {
            max_raster = current;
        }
    }

    return (max_raster > 280) ? VIDEO_PAL : VIDEO_NTSC;
#elif defined(__VIC20__)
    /* The VIC-20's own VIC chip only exposes an 8-bit raster line (see
     * _vic.h) - not enough range to reconstruct a true line number the way
     * the cases above do, so there's no reliable way to tell its 261-line
     * NTSC frame apart from its 311-line PAL one this way. Honest "don't
     * know" rather than a guess dressed up as a measurement. */
    return VIDEO_UNKNOWN;
#elif defined(__PET__) || defined(__CBM610__)
    /* No video standard to detect - both are driven by a monochrome
     * text/CRTC display (see pet.h/cbm610.h), not a PAL/NTSC colour signal. */
    return VIDEO_MONOCHROME;
#else
    return VIDEO_UNKNOWN;
#endif
}

const char *video_name(VideoSystem video)
{
    switch (video)
    {
        case VIDEO_PAL:
            return "PAL";

        case VIDEO_NTSC:
            return "NTSC";

        case VIDEO_MONOCHROME:
            return "Monochrome";

        default:
            return "Unknown";
    }
}

void video_get_text_size(unsigned *cols, unsigned *rows)
{
    unsigned char c, r;

    screensize(&c, &r);
    *cols = c;
    *rows = r;
}

unsigned video_gfx_width(void)
{
#if defined(__C64__) || defined(__C128__) || defined(__PLUS4__) || defined(__C16__) || defined(__CBM510__)
    return 320;
#elif defined(__VIC20__)
    return 176;
#else
    return 0; /* PET/CBM610: text-only, no bitmap graphics mode. */
#endif
}

unsigned video_gfx_height(void)
{
#if defined(__C64__) || defined(__C128__) || defined(__PLUS4__) || defined(__C16__) || defined(__CBM510__)
    return 200;
#elif defined(__VIC20__)
    return 184;
#else
    return 0;
#endif
}

unsigned video_colour_count(void)
{
#if defined(__C64__) || defined(__C128__) || defined(__CBM510__)
    return 16;
#elif defined(__VIC20__)
    return 8;
#elif defined(__PLUS4__) || defined(__C16__)
    return 121; /* TED: 16 luminance x 8 hue combinations minus overlaps. */
#else
    return 1; /* PET/CBM610: monochrome. */
#endif
}
