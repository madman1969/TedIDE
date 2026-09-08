#include <conio.h>
#include <c128.h>
#include <cbm.h>
#include "screen.h"

void screen_init(void)
{
    videomode(VIDEOMODE_80COL);
    clrscr();
    cursor(1);

    /* The C128 boots into the uppercase/graphics PETSCII character set,
     * where Shift+letter sends a graphics symbol rather than lowercase.
     * Switching to the lower/upper charset makes unshifted letters show
     * as lowercase and Shift+letter show as uppercase, like a normal
     * keyboard - editor.c's input filter accepts both byte ranges. */
    cputc(CH_FONT_LOWER);
}

void screen_shutdown(void)
{
    cputc(CH_FONT_UPPER);
    textcolor(COLOR_WHITE);
    videomode(VIDEOMODE_40COL);
    clrscr();
}
