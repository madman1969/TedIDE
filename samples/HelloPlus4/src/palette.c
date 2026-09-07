#include <conio.h>
/* conio.h pulls this in transitively (via <target.h> -> <cbm.h>) since it's
 * the Plus/4's own platform header, but it's included directly here since
 * this file's whole point is showing off Plus/4-specific hardware. */
#include <plus4.h>
#include "palette.h"
#include "delay.h"
#include "input.h"

#define SWATCH_ROW 10

/* Base hues, in the same order as the BCOLOR_* constants in <cbm264.h>
 * (BCOLOR_BLACK = 0 .. BCOLOR_LIGHTGREEN = 15). "Lemon" really is what
 * cc65's own header calls it - Commodore's original documentation didn't
 * do much better. */
static const char* const hue_names[16] = {
    "black",       "white",       "red",         "cyan",
    "purple",      "green",       "blue",        "yellow",
    "orange",      "brown",       "lemon",       "light violet",
    "blue-green",  "light blue",  "dark blue",   "light green",
};

/* A TED color byte packs the hue into bits 0-3 and the luminance into bits
 * 4-6 - exactly what BCOLOR_x | CATTR_LUMAy builds in <cbm264.h>, just
 * computed here from loop counters instead of named constants. */
static void show_swatch(unsigned char hue, unsigned char luma)
{
    unsigned char rawcolor = (unsigned char)(hue | (luma << 4));

    bordercolor(rawcolor);
    bgcolor(rawcolor);
    textcolor(luma < 4 ? COLOR_WHITE : COLOR_BLACK);

    gotoxy(0, SWATCH_ROW);
    cprintf("%-14s luminance %u/7   ", hue_names[hue], luma);
}

unsigned char palette_demo(void)
{
    unsigned char hue, luma, quit = 0;

    textcolor(COLOR_WHITE);
    cputsxy(0, 4, "The TED chip's 121-color palette:");
    cputsxy(0, 5, "16 hues x 8 luminance levels (black stays black)");

    for (hue = 0; hue < 16 && !quit; hue++)
    {
        unsigned char luma_count = (hue == 0) ? 1 : 8;
        for (luma = 0; luma < luma_count && !quit; luma++)
        {
            show_swatch(hue, luma);
            delay_short();
            quit = input_quit_requested();
        }
    }

    bordercolor(COLOR_BLACK);
    bgcolor(COLOR_BLACK);
    textcolor(COLOR_WHITE);

    return quit;
}
