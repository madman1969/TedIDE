#include <conio.h>
#include "screen.h"

/* Every other Commodore target here (C64/C128/CBM510's VIC-II, VIC-20's
 * VIC-I, the monochrome PET/CBM610) boots into a dark screen, so white text
 * is readable. The C16 and Plus/4 (TED chip) are the odd ones out - they
 * default to a pale background instead, so COLOR_WHITE text would be
 * invisible there; use black on just those two targets. */
#if defined(__C16__) || defined(__PLUS4__)
#define BANNER_COLOR COLOR_BLACK
#else
#define BANNER_COLOR COLOR_WHITE
#endif

void screen_init(void)
{
    clrscr();
    cursor(0);
    textcolor(BANNER_COLOR);
    cputsxy(0, 0, "Tedide C64 Demo - press Q to quit");
}

void screen_shutdown(void)
{
    textcolor(BANNER_COLOR);
    clrscr();
    cursor(1);
}

void screen_put_char(unsigned char x, unsigned char y, char c, unsigned char color)
{
    textcolor(color);
    cputcxy(x, y, c);
}
