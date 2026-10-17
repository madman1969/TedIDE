/*
 * Displays part of the Commodore Plus/4's TED palette as a grid: each column
 * is one of hues 0-7, and each row one value 0-F of the colour byte's top
 * nibble. Bits 4-6 of that nibble are the luminance (0-7) and bit 7 makes the
 * cell flash, so rows 8-F repeat rows 0-7 flashing. Each cell is drawn as two
 * solid blocks with row/column labels, and the program waits for a keypress
 * before exiting.
 */
#include <main.h>

/* The Plus/4's text screen. COLOR_RAM ($0800, one colour byte per cell) comes
 * from cc65's <cbm264.h>, which conio.h already includes for this target. */
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
            /* A TED colour byte: hue in bits 0-3, luminance (and flash) above. */
            colour = (lum << 4) | hue;

            /* The cell's position in screen memory: rows start at row 2
             * (below the headings), 40 cells per row, each swatch 4 cells
             * wide, starting 4 cells in (after the row label). */
            offset =
                (lum + 2) * 40 +
                (hue * 4) + 4;

            /* Two solid blocks: screen code 0xA0 is a reversed space. */
            SCREEN_RAM[offset]     = 0xA0;
            SCREEN_RAM[offset + 1] = 0xA0;

            /* Colour them by writing the colour byte straight into colour RAM. */
            COLOR_RAM[offset]     = colour;
            COLOR_RAM[offset + 1] = colour;
        }
    }

    gotoxy(0, 20);
    cprintf("Rows=Luminance 0-F");
    gotoxy(0, 21);
    cprintf("Cols=Hue 0-7");

    /* Wait for a key so the grid stays on screen. */
    cgetc();
    return 0;
}
