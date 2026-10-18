#include <string.h>
#include <conio.h>
#include <plus4.h>
#include "farmem.h"
#include "screen.h"

static void wait_for_safe_copy(void);

/* The Plus/4's screen, making use of what its TED video chip has over the C64's VIC-II:
 *
 * - 121 colours: every shade of land and sea gets its own hue and brightness.
 * - Multicolour characters: most tiles are drawn with four colours a cell - black, a dark
 *   green and a light grey shared by every cell, and the cell's own colour - at half the
 *   horizontal resolution. Multicolour is set per cell, by bit 3 of its colour, so hi-res
 *   cells (towns, the cursor, the status bar's text) share the screen with them.
 * - Hardware flashing: bit 7 of a hi-res cell's colour makes the TED flash it, so towns and
 *   the cursor blink without the program doing anything.
 * - A movable screen: the TED reads the screen and its colours from any 2K of RAM, together,
 *   so the map is drawn into a hidden second screen and the TED switched to it in one go -
 *   no tearing. The C64 can't do this, as its colour RAM is fixed.
 * - Smooth scrolling: the TED's scroll registers shift the map up to 7 pixels each way. They
 *   shift the whole screen, so they're changed partway down each frame, below the status bar
 *   - see the interrupt handler in blit.s.
 */

/* Colour bytes: a hue in bits 0-3, a brightness in bits 4-6, flashing in bit 7. MC marks a
 * multicolour cell, which can only use the first 8 hues. */
#define MC      0x08
#define FLASH   0x80

const unsigned char view_tile_colour[TILE_COUNT] = {
    MC | BCOLOR_BLUE | CATTR_LUMA1,     /* deepest */
    MC | BCOLOR_BLUE | CATTR_LUMA2,     /* deeper */
    MC | BCOLOR_BLUE | CATTR_LUMA3,     /* deep */
    MC | BCOLOR_BLUE | CATTR_LUMA4,     /* sea */
    MC | BCOLOR_CYAN | CATTR_LUMA4,     /* shallows */
    MC | BCOLOR_YELLOW | CATTR_LUMA6,   /* beach */
    MC | BCOLOR_GREEN | CATTR_LUMA5,    /* grass */
    MC | BCOLOR_GREEN | CATTR_LUMA4,    /* upland */
    MC | BCOLOR_GREEN | CATTR_LUMA3,    /* forest */
    MC | BCOLOR_GREEN | CATTR_LUMA2,    /* high forest */
    MC | BCOLOR_YELLOW | CATTR_LUMA2,   /* hills: a dark yellow, as brown isn't a multicolour hue */
    MC | BCOLOR_YELLOW | CATTR_LUMA1,   /* high hills */
    MC | BCOLOR_WHITE | CATTR_LUMA2,    /* mountain: a dark grey */
    MC | BCOLOR_WHITE | CATTR_LUMA4,    /* high mountain */
    MC | BCOLOR_WHITE | CATTR_LUMA7,    /* snow */
    FLASH | BCOLOR_RED | CATTR_LUMA5,   /* town: hi-res, flashing */
    MC | BCOLOR_CYAN | CATTR_LUMA5,     /* river: brighter than the shallows it runs into */
};
const unsigned char screen_cursor_colour = FLASH | BCOLOR_WHITE | CATTR_LUMA7;

/* The colours every multicolour cell shares: its pixels are 00 the background, 01 the first,
 * 10 the second and 11 the cell's own colour. */
#define SHARED_DARK     (BCOLOR_GREEN | CATTR_LUMA1)
#define SHARED_LIGHT    (BCOLOR_WHITE | CATTR_LUMA6)

/* The multicolour pictures, in GLYPH_ order up to GLYPH_RIVER: 4 double-width pixels a row, two
 * bits each, bits 7-6 the leftmost. Drawn as text, with '.' black, 'd' dark green, 'w' light
 * grey and '#' the cell's colour - each picture's rows are given beside it. Towns and the
 * cursor stay hi-res, from tile_glyphs. */
static const unsigned char mc_glyphs[GLYPH_TOWN][8] = {
    { 0xFF, 0xEF, 0xFF, 0xFF, 0xFE, 0xFF, 0xFF, 0xFF },   /* deep sea:  #### #w## #### #### ###w #### #### #### */
    { 0xFF, 0xAF, 0xFF, 0xFF, 0xFA, 0xFF, 0xFF, 0xFF },   /* sea:       #### ww## #### #### ##ww #### #### #### */
    { 0xFF, 0xFB, 0xFF, 0xBF, 0xFF, 0xFE, 0xFF, 0xEF },   /* beach:     #### ##w# #### w### #### ###w #### #w## */
    { 0xFF, 0xDF, 0xFF, 0xFD, 0xFF, 0x7F, 0xFF, 0xF7 },   /* grass:     #### #d## #### ###d #### d### #### ##d# */
    { 0xDD, 0xFF, 0x77, 0xFF, 0xDD, 0xFF, 0x77, 0xFF },   /* forest:    #d#d #### d#d# #### #d#d #### d#d# #### */
    { 0x00, 0x30, 0xF4, 0xFF, 0xFF, 0xDF, 0xFF, 0xF7 },   /* hills:     .... .#.. ##d. #### #### #d## #### ##d# */
    { 0x20, 0x38, 0xFB, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },   /* mountain:  .w.. .#w. ##w# #### #### #### #### #### */
    { 0xFF, 0xEF, 0xFF, 0xFB, 0xFF, 0xBF, 0xFF, 0xFE },   /* snow:      #### #w## #### ##w# #### w### #### ###w */
    { 0xFF, 0xEB, 0xFF, 0xBE, 0xFF, 0xEB, 0xFF, 0xBE },   /* river:     #### #ww# #### w##w #### #ww# #### w##w */
};

/* TED registers and bits, beyond cc65's TED struct names. */
#define TED_CHARS_ROM   0x04        /* $FF12 bit 2: characters from ROM, not RAM */
#define TED_CHAR_BASE   0xFC        /* $FF13 bits 2-7: the character set's address / 1K */
#define TED_MCM         0x10        /* $FF07 bit 4: multicolour mode */
#define TED_CSEL        0x08        /* $FF07 bit 3: 40 columns, not 38 */
#define TED_KEEP_07     0xC0        /* $FF07 bits 6-7: PAL/NTSC and 256 characters, kept */
#define TED_DEN         0x10        /* $FF06 bit 4: display on */
#define TED_RSEL        0x08        /* $FF06 bit 3: 25 rows, not 24 */
#define TED_YSCROLL_25  0x03        /* $FF06's scroll for an unscrolled 25-row screen */
#define TED_TIMER2      0x10        /* $FF09/$FF0A bit 4: timer 2's interrupt */

#define FONT_SIZE       0x400       /* 128 characters */
#define SCREEN_SIZE     0x800       /* 1K of colours, then 1K of characters */

/* The interrupt handler's settings (blit.s), all register values ready to store:
 * ted_top06/07 for the status bar (or the whole text screen), ted_map06/07 for the map below
 * it, without their scroll bits; ted_want_x/y the map's scroll, applied from the next frame;
 * ted_flip the $FF14 value of a screen to switch to at the next frame, or 0; ted_split
 * non-zero while the map is on show. */
extern unsigned char ted_top06, ted_top07, ted_map06, ted_map07;
extern unsigned char ted_want_x, ted_want_y;
extern volatile unsigned char ted_flip;
extern unsigned char ted_split;
extern volatile unsigned char ted_frames;   /* counted up at the bottom of each frame */

/* Copies the ROM's 1K uppercase font to dst (blit.s): the ROMs have to be paged in to read it,
 * and the C runtime's stack is under them at $FCFF, so it's done in assembly. */
void __fastcall__ screen_copy_font(unsigned char* dst);

/* Room for the second screen and the character set: the screen needs 2K on a 2K boundary, the
 * characters 1K on a 1K boundary, so this has room for both wherever it starts. The TED reads
 * the screen from ROM instead of RAM above $8000 while the ROMs are paged in, as they are in
 * every interrupt, so the screen must end below $8000 - the static array, in the BSS below the
 * heap, normally does; screen_init checks. */
static unsigned char video_space[SCREEN_SIZE * 2 + FONT_SIZE - 1];

static unsigned char* screens[2];       /* each screen's colours, then its characters */
static unsigned char showing;           /* which screen the TED is showing */
static unsigned char double_buffered;

unsigned char* screen_draw_chars;
unsigned char* screen_draw_colours;

static unsigned char saved_misc, saved_char_addr, saved_06, saved_07, saved_14;

/* The $FF14 value that shows a screen: the top 5 bits of its address. */
static unsigned char video_reg(const unsigned char* screen)
{
    return (unsigned char)((unsigned)screen >> 8) & 0xF8;
}

/* Points screen_draw_chars/colours at the screen that isn't showing - or, without a second
 * screen, the one that is. */
static void draw_to_hidden(void)
{
    unsigned char* hidden = screens[double_buffered ? showing ^ 1 : 0];
    screen_draw_colours = hidden;
    screen_draw_chars = hidden + 0x400;
}

/* Waits for the interrupt handler to make a requested switch of screens. */
static void wait_for_flip(void)
{
    while (ted_flip)
        ;
}

void screen_init(void)
{
    unsigned char* chars;
    unsigned char* second;
    unsigned char i;

    /* The second screen on the first 2K boundary in video_space, the characters after it. */
    second = (unsigned char*)(((unsigned)video_space + SCREEN_SIZE - 1) & ~(SCREEN_SIZE - 1));
    chars = second + SCREEN_SIZE;
    screens[0] = (unsigned char*)SCREEN_COLOURS;
    screens[1] = second;
    double_buffered = (unsigned)second + SCREEN_SIZE <= 0x8000;

    /* The ROM's font with the pictures drawn over some of its graphics characters: multicolour
     * ones for the land and sea, hi-res ones for towns and the cursor. */
    far_set_copy_hook(wait_for_safe_copy);
    screen_copy_font(chars);
    memcpy(chars + SCREEN_FIRST_GLYPH * 8, mc_glyphs, sizeof(mc_glyphs));
    for (i = GLYPH_TOWN; i < GLYPH_COUNT; ++i)
        memcpy(chars + (SCREEN_FIRST_GLYPH + i) * 8, tile_glyphs[i], 8);

    clrscr();
    saved_misc = TED.misc;
    saved_char_addr = TED.char_addr;
    saved_06 = TED.vscroll;
    saved_07 = TED.hscroll;
    saved_14 = TED.video_addr;

    TED.char_addr = (TED.char_addr & ~TED_CHAR_BASE) | ((unsigned)chars >> 8);
    TED.misc &= ~TED_CHARS_ROM;
    TED.color1 = SHARED_DARK;
    TED.color2 = SHARED_LIGHT;

    /* The interrupt handler's settings: the status bar is plain 40x25 text; the map below it
     * is 38x24, so the columns and rows its scrolling uncovers at the edges stay hidden. */
    ted_top06 = TED_DEN | TED_RSEL | TED_YSCROLL_25;
    ted_top07 = (saved_07 & TED_KEEP_07) | TED_MCM | TED_CSEL;
    ted_map06 = TED_DEN;
    ted_map07 = (saved_07 & TED_KEEP_07) | TED_MCM;
    ted_split = 0;
    ted_flip = 0;
    showing = 0;
    draw_to_hidden();
}

void screen_done(void)
{
    far_set_copy_hook(0);
    screen_text();
    TED.imr &= ~TED_TIMER2;
    TED.vscroll = saved_06;
    TED.hscroll = saved_07;
    TED.video_addr = saved_14;
    TED.misc = (TED.misc & ~TED_CHARS_ROM) | (saved_misc & TED_CHARS_ROM);
    TED.char_addr = (TED.char_addr & ~TED_CHAR_BASE) | (saved_char_addr & TED_CHAR_BASE);
}

void screen_text(void)
{
    /* Back to the first screen, which conio writes to, with no split or scrolling. */
    ted_split = 0;
    if (showing != 0)
    {
        ted_flip = video_reg(screens[0]);
        wait_for_flip();
        showing = 0;
    }
    TED.imr &= ~TED_TIMER2;
    draw_to_hidden();
}

void screen_map(void)
{
    unsigned char s;

    /* Both screens' status rows in grey and the blank row below them black, ready for the
     * status text view.c writes each frame. */
    for (s = 0; s < 2; ++s)
    {
        memset(screens[s] + SCREEN_STATUS_ROW * SCREEN_COLS, COLOR_GRAY3, 2 * SCREEN_COLS);
        memset(screens[s] + (SCREEN_STATUS_ROW + 2) * SCREEN_COLS, COLOR_BLACK, SCREEN_COLS);
        memset(screens[s] + 0x400 + (SCREEN_STATUS_ROW + 2) * SCREEN_COLS, ' ', SCREEN_COLS);
    }
    /* Timer 2 runs on from wherever it got to before: give it its longest count, so that it
     * doesn't fire until the next frame's bottom has started it properly, and forget any
     * interrupt it already raised. */
    TED.t2_lo = 0xFF;
    TED.t2_hi = 0xFF;
    TED.irr = TED_TIMER2;
    TED.imr |= TED_TIMER2;
    ted_split = 1;
}

/* Sets the map's scroll for the interrupt handler to apply from the next frame. The scroll
 * registers shift the screen right and down, so a view further into its top-left cell needs
 * less of a shift: 7 - fine. */
static void set_scroll(unsigned char fine_x, unsigned char fine_y)
{
    ted_want_x = 7 - fine_x;
    ted_want_y = 7 - fine_y;
}

void screen_present(unsigned char fine_x, unsigned char fine_y)
{
    set_scroll(fine_x, fine_y);
    if (!double_buffered)
        return;
    /* Switch screens at the bottom of the next frame - with the new scroll - then draw into the
     * other one. Waiting for the switch also paces the scrolling to the frame rate. */
    showing ^= 1;
    ted_flip = video_reg(screens[showing]);
    wait_for_flip();
    draw_to_hidden();
}

/* The raster lines on which a copy from a RAM expansion may not start, because it runs with
 * interrupts off and would still be running when the split at line 22 is due, making it late
 * (see blit.s): a copy of up to 256 bytes takes up to about 65 lines, and the split itself is done
 * by about line 26. Only the copy matters: FarMem's own work around it - checking its window cache,
 * working out the bank - leaves interrupts on, so FarMem calls wait_for_safe_copy (its copy hook)
 * just before each copy, not before each read. */
#define COPY_UNSAFE_FROM  250
#define COPY_UNSAFE_TO    30

/* The raster line the beam is on, 0-311. */
static unsigned raster_line(void)
{
    unsigned char lo, hi;

    /* Read the high bit either side of the low byte, in case the line moved on between. */
    do
    {
        hi = TED.rasterline_hi & 1;
        lo = TED.rasterline_lo;
    }
    while ((TED.rasterline_hi & 1) != hi);
    return ((unsigned)hi << 8) | lo;
}

/* What to do while a copy waits for the split (screen_set_copy_wait_work), or 0. */
static void (*copy_wait_work)(void);

void screen_set_copy_wait_work(void (*work)(void))
{
    copy_wait_work = work;
}

/* FarMem's copy hook: waits while a copy started now could delay the split - doing the program's
 * copy_wait_work first, if there's one, when that's in the lower border, before the frame's top. */
static void wait_for_safe_copy(void)
{
    unsigned line;

    if (!ted_split)
        return;
    if (copy_wait_work && raster_line() >= COPY_UNSAFE_FROM)
        copy_wait_work();
    do
        line = raster_line();
    while (line >= COPY_UNSAFE_FROM || line <= COPY_UNSAFE_TO);
}

/* A frame ends, as far as the program is concerned, at the interrupt at line 204: that's when
 * the next step's wait (screen_scroll) is over. */
#define FRAME_END_LINE  204

/* The raster lines left before the frame ends. */
static unsigned lines_left(void)
{
    unsigned line = raster_line();
    return line < FRAME_END_LINE ? FRAME_END_LINE - line : FRAME_END_LINE + 312 - line;
}

/* The last line a read can start on and have FarMem's work before the copy done by the time the
 * copy can start, after the split: measured at up to about 60 lines here, in the border, where the
 * CPU runs at twice the speed it has while the TED is fetching the screen. */
#define READ_START_BY  250

unsigned char screen_far_read_lines(void)
{
    unsigned line;

    if (!ted_split)
        return 255;
    /* From the frame's end until READ_START_BY: the copy hook (wait_for_safe_copy) holds the copy
     * until the split is past, and it then has until the frame's end, which comes well before a
     * copy would have to wait again (COPY_UNSAFE_FROM). Any later and FarMem's work would still be
     * going on then, eating into that time; earlier, in the frame, and it would all be. */
    line = raster_line();
    if (line < FRAME_END_LINE || line > READ_START_BY)
        return 0;
    return FRAME_END_LINE - COPY_UNSAFE_TO - 1;
}

unsigned char screen_time_for(unsigned char lines)
{
    return lines_left() > lines;
}

void screen_scroll(unsigned char fine_x, unsigned char fine_y)
{
    unsigned char frame = ted_frames;

    /* Then wait for the frame that applies it, to pace the scrolling. */
    set_scroll(fine_x, fine_y);
    while (ted_frames == frame)
        ;
}
