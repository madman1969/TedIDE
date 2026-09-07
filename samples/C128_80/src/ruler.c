#include <conio.h>
#include "ruler.h"
#include "input.h"

void ruler_draw(unsigned char row)
{
    unsigned char columns, rows, x;

    screensize(&columns, &rows);
    for (x = 0; x < columns; x++)
        cputcxy(x, row, '0' + (x % 10));
}

unsigned char ruler_demo(void)
{
    unsigned char columns, rows;

    textcolor(COLOR_WHITE);
    cputsxy(0, 4, "screensize() reports the real width below - a ruler");
    cputsxy(0, 5, "spanning every column proves it's genuinely there:");

    screensize(&columns, &rows);
    gotoxy(0, 7);
    cprintf("Screen: %ux%u", columns, rows);

    ruler_draw(9);

    return input_quit_requested();
}
