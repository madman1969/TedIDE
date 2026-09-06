#include <conio.h>
#include "screen.h"

void screen_init(void)
{
    clrscr();
    cursor(0);
    textcolor(COLOR_WHITE);
    cputsxy(0, 0, "Tedide C64 Demo - press Q to quit");
}

void screen_shutdown(void)
{
    textcolor(COLOR_WHITE);
    clrscr();
    cursor(1);
}

void screen_put_char(unsigned char x, unsigned char y, char c, unsigned char color)
{
    textcolor(color);
    cputcxy(x, y, c);
}
