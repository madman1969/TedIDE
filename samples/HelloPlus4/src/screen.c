#include <conio.h>
#include "screen.h"

void screen_init(void)
{
    clrscr();
    cursor(0);
}

void screen_banner(void)
{
    textcolor(COLOR_WHITE);
    cputsxy(0, 0, "Tedide Plus/4 Demo - TED chip feature tour");
}

void screen_shutdown(void)
{
    textcolor(COLOR_WHITE);
    clrscr();
    cursor(1);
}
