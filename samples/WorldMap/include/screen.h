#ifndef SCREEN_H
#define SCREEN_H

/* The machine-specific half of drawing the map: where the screen is laid out, the tiles'
 * characters and colours, and how a finished frame reaches the screen. screen_c64.c and
 * screen_plus4.c each provide it, and each project builds one of them. */

#include "world.h"

#if defined(__C64__)
/* The C64's video chip can only read a character set from the 16K bank it shows, and in the
 * usual bank the program fills every free slot, so the screen moves to the RAM under the
 * KERNAL ROM - see screen_c64.c. Colour RAM is fixed at $D800. */
#  define SCREEN_CHARS        0xE000
#  define SCREEN_COLOURS      0xD800
/* Reversed characters, which no text here uses. */
#  define SCREEN_FIRST_GLYPH  0x80
/* The map fills the top 23 rows, the status bar the last two. It moves a whole cell at a time:
 * positions have no fraction of a cell. */
#  define SCREEN_MAP_TOP      0
#  define SCREEN_MAP_HEIGHT   23
#  define SCREEN_STATUS_ROW   23
#  define SCREEN_FINE_BITS    0
#  define SCREEN_STEP         1       /* how far the joystick moves the view each frame */
#  define SCREEN_ROW_SHIFT(fine_y)  0
#else
/* The Plus/4's TED keeps the screen where it is, at $0C00 with its colours at $0800, and reads
 * the character set from any 1K of RAM - see screen_plus4.c. Its 128-character mode shows codes
 * from $80 as reversed copies of the first 128, so the tiles replace graphics characters
 * instead, which no text here uses either. */
#  define SCREEN_CHARS        0x0C00
#  define SCREEN_COLOURS      0x0800
#  define SCREEN_FIRST_GLYPH  0x60
/* The status bar takes the top two rows, then a blank row hides the join where the TED's
 * vertical scroll changes mid-frame, and the map has the 22 rows below. It scrolls a pixel at
 * a time: positions count eighths of a cell. */
#  define SCREEN_MAP_TOP      3
#  define SCREEN_MAP_HEIGHT   22
#  define SCREEN_STATUS_ROW   0
#  define SCREEN_FINE_BITS    3
#  define SCREEN_STEP         2       /* pixels */
/* When the vertical scroll is set below the TED's default of 3 mid-frame - for views 5 to 7
 * pixels into a cell - the TED repeats the blank row once, showing the whole map a character
 * row lower than it should (measured in VICE). The map is drawn a row further down the world to
 * make up for it, and its top edge moves within the gap under the status bar instead. */
#  define SCREEN_ROW_SHIFT(fine_y)  ((fine_y) >= 5)
#endif

#define SCREEN_COLS    40

/* The pictures the tiles share - every shade of sea uses the wave picture, for instance - in
 * the order of tile_glyph's values, then the cursor. */
#define GLYPH_DEEP      0
#define GLYPH_SEA       1
#define GLYPH_BEACH     2
#define GLYPH_GRASS     3
#define GLYPH_FOREST    4
#define GLYPH_HILLS     5
#define GLYPH_MOUNTAIN  6
#define GLYPH_SNOW      7
#define GLYPH_TOWN      8
#define GLYPH_CURSOR    9
#define GLYPH_COUNT     10

/* Each tile's picture (tiles.c). */
extern const unsigned char tile_glyph[TILE_COUNT];

/* The pictures, 8x8 hi-res pixels, shared by both machines (tiles.c). The Plus/4 draws most of
 * its tiles in multicolour instead - see screen_plus4.c. */
extern const unsigned char tile_glyphs[GLYPH_COUNT][8];

/* Each tile's colour byte, and the cursor's - not static: blit.s reads view_tile_colour. The
 * C64's are its 16 colours; the Plus/4's pick a hue and a brightness each from its 121, turn on
 * multicolour for most tiles and flashing for towns and the cursor. */
extern const unsigned char view_tile_colour[TILE_COUNT];
extern const unsigned char screen_cursor_colour;

/* Where the next map frame is drawn: the screen itself on the C64, and on the Plus/4 whichever
 * of its two screens isn't showing. */
extern unsigned char* screen_draw_chars;
extern unsigned char* screen_draw_colours;

/* Builds the character set from the ROM's and the tiles, points the video chip and the KERNAL
 * at it, and clears the screen. */
void screen_init(void);

/* Puts the character set and screen back as they were. */
void screen_done(void);

/* The plain text screen, for the overviews: no scrolling, at SCREEN_CHARS. */
void screen_text(void);

/* The map screen: the status bar and the scrolling map. */
void screen_map(void);

/* Shows the frame just drawn at screen_draw_chars, scrolled fine_x and fine_y pixels (0-7) into
 * its top-left cell, and makes screen_draw_chars the next one to draw into. On the C64, which
 * draws straight onto the screen, there's nothing to do. */
void screen_present(unsigned char fine_x, unsigned char fine_y);

/* Waits, if need be, until it's safe to read far memory without spoiling the frame on show.
 * On the Plus/4, a copy from a RAM expansion runs with interrupts off, and one that overlapped
 * the split under the status bar would make it late; the C64 has nothing to wait for. */
void screen_before_far_read(void);

/* Scrolls the frame on show to fine_x and fine_y without drawing a new one - for moves that
 * stay within the same cell - and waits for the next frame, so a move a frame keeps pace with
 * the screen. Not used on the C64. */
void screen_scroll(unsigned char fine_x, unsigned char fine_y);

#endif
