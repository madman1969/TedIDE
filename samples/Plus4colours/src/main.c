/*
 * Displays the Commodore Plus/4's full TED palette as a grid: each row is
 * one of the 16 luminance levels, each column one of the 8 hues, drawn as
 * solid colour blocks with row/column labels. Waits for a keypress before
 * exiting.
 */
#include <main.h>

#define SCREEN_RAM ((uint8_t*)0x0C00)
// #define COLOR_RAM  ((uint8_t*)0x0800)

int main(void)
{
    unsigned char lum;
    unsigned char hue;
    unsigned char colour;
    unsigned int offset;

    clrscr();

    cprintf("PLUS/4 TED PALETTE\n");

    /* Column headings */
    gotoxy(4, 1);
    for (hue = 0; hue < 8; ++hue)
    {
        cprintf(" %X ", hue);
    }

    for (lum = 0; lum < 16; ++lum)
    {
        /* Row label */
        gotoxy(0, lum + 2);
        cprintf("%X", lum);

        for (hue = 0; hue < 8; ++hue)
        {
            colour = (lum << 4) | hue;

            offset =
                (lum + 2) * 40 +
                (hue * 4) + 4;

            /* Two solid blocks */
            SCREEN_RAM[offset]     = 0xA0;
            SCREEN_RAM[offset + 1] = 0xA0;

            COLOR_RAM[offset]     = colour;
            COLOR_RAM[offset + 1] = colour;
        }
    }

    gotoxy(0, 20);
    cprintf("Rows=Luminance 0-F");
    gotoxy(0, 21);
    cprintf("Cols=Hue 0-7");

    cgetc();
    return 0;
}