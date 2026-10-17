#ifndef SCREEN_H
#define SCREEN_H

/* The machine-specific half of drawing the map: where the text screen and its colours are, the
 * tiles' characters and colours, and a character set with the tiles drawn into it.
 * screen_c64.c and screen_plus4.c each provide it, and each project builds one of them. */

#include "world.h"

#if defined(__C64__)
/* The C64's video chip can only read a character set from the 16K bank it shows, and in the
 * usual bank the program fills every free slot, so the screen moves to the RAM under the
 * KERNAL ROM - see screen_c64.c. Colour RAM is fixed at $D800. */
#  define SCREEN_CHARS        0xE000
#  define SCREEN_COLOURS      0xD800
/* Reversed characters, which no text here uses. */
#  define SCREEN_FIRST_TILE   0x80
#else
/* The Plus/4's TED keeps the screen where it is, at $0C00 with its colours at $0800, and reads
 * the character set from any 1K of RAM - see screen_plus4.c. Its 128-character mode shows codes
 * from $80 as reversed copies of the first 128, so the tiles replace graphics characters
 * instead, which no text here uses either. */
#  define SCREEN_CHARS        0x0C00
#  define SCREEN_COLOURS      0x0800
#  define SCREEN_FIRST_TILE   0x60
#endif

#define SCREEN_COLS    40
#define SCREEN_CURSOR  (SCREEN_FIRST_TILE + TILE_COUNT)

/* Each tile's colour - not static: blit.s reads it. The C64's are its 16 colours; the Plus/4's
 * pick a hue and a brightness each from its 121. */
extern const unsigned char view_tile_colour[TILE_COUNT];

/* The tiles' and the cursor's 8x8 pictures, shared by both machines (tiles.c). */
extern const unsigned char tile_glyphs[TILE_COUNT + 1][8];

/* Builds the character set from the ROM's and the tiles, points the video chip and the KERNAL
 * at it and at the screen, and clears the screen. */
void screen_init(void);

/* Puts the character set and screen back as they were. */
void screen_done(void);

#endif
