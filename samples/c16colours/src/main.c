/*
 * Displays all 16 of the Commodore 16's colours as a strip of blocks
 * across the screen, each labelled with its colour ID (0-F). Waits for a
 * keypress before exiting.
 */
#include <main.h>

#define SCREEN_RAM ((uint8_t*)0x0C00)
// #define COLOR_RAM  ((uint8_t*)0x0800)

int main(void)
{
    unsigned char colour;
    unsigned char x;
    unsigned int offset;

    clrscr();

    cprintf("C16 TED Colour Demo\n\n");

    for (colour = 0; colour < 16; ++colour)
    {
        /* Draw a 2-row colour block */
        for (x = 0; x < 5; ++x)
        {
            offset = colour * 5 + x;

            SCREEN_RAM[offset + 80]  = 0xA0;
            SCREEN_RAM[offset + 120] = 0xA0;

            COLOR_RAM[offset + 80]  = colour;
            COLOR_RAM[offset + 120] = colour;
        }

        gotoxy(colour * 2, 8);
        cprintf("%X", colour);
    }

    cprintf("\n\nColour IDs 0-F");
    cgetc();

    return 0;
}