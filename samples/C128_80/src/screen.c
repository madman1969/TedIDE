#include <conio.h>
#include <c128.h>
#include "screen.h"

void screen_init(void)
{
    videomode(VIDEOMODE_80COL);
    clrscr();
    cursor(0);
}

void screen_banner(void)
{
    textcolor(COLOR_WHITE);
    cputsxy(0, 0, "Tedide C128 Demo - 80-column (VDC) feature tour");
}

void screen_shutdown(void)
{
    textcolor(COLOR_WHITE);
    videomode(VIDEOMODE_40COL);
    clrscr();
    cursor(1);
}
