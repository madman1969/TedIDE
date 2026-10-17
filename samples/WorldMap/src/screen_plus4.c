#include <stdlib.h>
#include <string.h>
#include <conio.h>
#include <plus4.h>
#include "screen.h"

/* The TED reads its character set from any 1K of RAM, so the screen stays where it is and only
 * the characters move: a copy of the ROM's uppercase set, with the tiles drawn over some of its
 * graphics characters. */

/* A hue and a brightness each, from the TED's 121 colours. */
const unsigned char view_tile_colour[TILE_COUNT] = {
    BCOLOR_BLUE | CATTR_LUMA2,      /* deep sea */
    BCOLOR_BLUE | CATTR_LUMA4,      /* sea */
    BCOLOR_YELLOW | CATTR_LUMA5,    /* beach */
    BCOLOR_GREEN | CATTR_LUMA3,     /* grass */
    BCOLOR_GREEN | CATTR_LUMA1,     /* forest */
    BCOLOR_BROWN | CATTR_LUMA3,     /* hills */
    BCOLOR_WHITE | CATTR_LUMA3,     /* mountain: grey */
    BCOLOR_WHITE | CATTR_LUMA7,     /* snow */
    BCOLOR_RED | CATTR_LUMA4,       /* town */
};

#define FONT_SIZE       0x400       /* 128 characters */
#define TED_CHARS_ROM   0x04        /* $FF12 bit 2: characters from ROM, not RAM */
#define TED_CHAR_BASE   0xFC        /* $FF13 bits 2-7: the character set's address / 1K */

/* Copies the ROM's 1K uppercase font to dst (blit.s): the ROMs have to be paged in to read it,
 * and the C runtime's stack is under them at $FCFF, so it's done in assembly. */
void __fastcall__ screen_copy_font(unsigned char* dst);

static unsigned char saved_misc;
static unsigned char saved_char_addr;

void screen_init(void)
{
    /* The character set has to start on a 1K boundary: allocating 2K - 1 always has one. It's
     * never freed - the program keeps it to the end. */
    unsigned char* chars = (unsigned char*)(((unsigned)malloc(FONT_SIZE * 2 - 1) + FONT_SIZE - 1) & ~(FONT_SIZE - 1));

    screen_copy_font(chars);
    memcpy(chars + SCREEN_FIRST_TILE * 8, tile_glyphs, sizeof(tile_glyphs));

    clrscr();
    saved_misc = TED.misc;
    saved_char_addr = TED.char_addr;
    TED.char_addr = (TED.char_addr & ~TED_CHAR_BASE) | ((unsigned)chars >> 8);
    TED.misc &= ~TED_CHARS_ROM;
}

void screen_done(void)
{
    TED.misc = (TED.misc & ~TED_CHARS_ROM) | (saved_misc & TED_CHARS_ROM);
    TED.char_addr = (TED.char_addr & ~TED_CHAR_BASE) | (saved_char_addr & TED_CHAR_BASE);
}
