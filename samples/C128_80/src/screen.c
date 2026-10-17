#include <conio.h>
#include <c128.h>
#include "screen.h"

void screen_init(void)
{
    /* Send conio's output to the VDC's 80-column screen. */
    videomode(VIDEOMODE_80COL);
    clrscr();
    cursor(0);
}

void screen_banner(void)
{
    textcolor(COLOR_WHITE);
    cputsxy(0, 0, "Tedide C128 Demo - 80-column (VDC) feature tour");
}

void screen_prompt(void)
{
    textcolor(COLOR_WHITE);
    cputsxy(0, 23, "Any key: next demo   Q: quit");
}

void screen_shutdown(void)
{
    /* Leave the machine in 40-column mode, as BASIC normally starts. */
    textcolor(COLOR_WHITE);
    videomode(VIDEOMODE_40COL);
    clrscr();
    cursor(1);
}
