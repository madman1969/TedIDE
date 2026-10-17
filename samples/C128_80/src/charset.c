#include <conio.h>
#include "charset.h"
#include "vdc.h"
#include "input.h"
#include "screen.h"

/* Reversed graphics characters this program never prints. */
#define CODE_INVADER  0xF0
#define CODE_SMILEY   0xF1
#define CODE_HEART    0xF2
#define CODE_SHIP     0xF3

#define BYTES_PER_GLYPH   16   /* the VDC stores 16 rows per character; 8 are shown */
#define ALT_CHARSET_OFFSET 0x1000
#define FRAME_TICKS       16
#define INVADER_TOP       10
#define INVADER_ROWS      5

static const unsigned char invader_frames[2][8] = {
    { 0x18, 0x3C, 0x7E, 0xDB, 0xFF, 0x24, 0x5A, 0xA5 },
    { 0x18, 0x3C, 0x7E, 0xDB, 0xFF, 0x5A, 0x81, 0x42 },
};

static const unsigned char smiley[8] = { 0x3C, 0x42, 0xA5, 0x81, 0xA5, 0x99, 0x42, 0x3C };
static const unsigned char heart[8]  = { 0x66, 0xFF, 0xFF, 0xFF, 0x7E, 0x3C, 0x18, 0x00 };
static const unsigned char ship[8]   = { 0x18, 0x18, 0x3C, 0x7E, 0xFF, 0xFF, 0x24, 0x00 };

static const unsigned char invader_colours[INVADER_ROWS] = {
    VDC_LIGHT_RED, VDC_LIGHT_PURPLE, VDC_LIGHT_CYAN, VDC_LIGHT_GREEN, VDC_LIGHT_YELLOW,
};

static unsigned char invader_row[VDC_COLUMNS];

static void define_glyph(unsigned glyphs, unsigned char code, const unsigned char* rows)
{
    vdc_write_bytes(glyphs + code * BYTES_PER_GLYPH, rows, 8);
}

static void show_glyph(unsigned char x, unsigned char code, unsigned char attr)
{
    vdc_set_chars(x, 7, code, 1);
    vdc_set_attrs(x, 7, attr, 1);
}

unsigned char charset_demo(void)
{
    unsigned char charset, i, frame, ticks;
    unsigned glyphs, screen;

    textcolor(COLOR_WHITE);
    cputsxy(0, 4, "The VDC's character set lives in its own RAM, so any glyph can be redrawn -");
    cputsxy(0, 5, "and every cell showing that character changes at the same moment:");
    cputsxy(0, 7, "Four new glyphs:");
    cputsxy(0, 16, "One glyph, redefined three times a second, animates all 200 invaders.");
    screen_prompt();

    charset = vdc_text_charset();
    glyphs = vdc_charset_base() + (charset ? ALT_CHARSET_OFFSET : 0);

    define_glyph(glyphs, CODE_INVADER, invader_frames[0]);
    define_glyph(glyphs, CODE_SMILEY, smiley);
    define_glyph(glyphs, CODE_HEART, heart);
    define_glyph(glyphs, CODE_SHIP, ship);

    show_glyph(18, CODE_INVADER, charset | VDC_LIGHT_GREEN);
    show_glyph(20, CODE_SMILEY, charset | VDC_LIGHT_YELLOW);
    show_glyph(22, CODE_HEART, charset | VDC_LIGHT_RED);
    show_glyph(24, CODE_SHIP, charset | VDC_LIGHT_CYAN);

    for (i = 0; i < VDC_COLUMNS; ++i)
        invader_row[i] = (i & 1) ? ' ' : CODE_INVADER;

    screen = vdc_screen_base();
    for (i = 0; i < INVADER_ROWS; ++i)
    {
        vdc_write_bytes(screen + (INVADER_TOP + i) * VDC_COLUMNS, invader_row, VDC_COLUMNS);
        vdc_set_attrs(0, INVADER_TOP + i, charset | invader_colours[i], VDC_COLUMNS);
    }

    frame = 0;
    ticks = 0;
    while (!kbhit())
    {
        vdc_wait_vblank();
        if (++ticks == FRAME_TICKS)
        {
            ticks = 0;
            frame ^= 1;
            define_glyph(glyphs, CODE_INVADER, invader_frames[frame]);
        }
    }

    vdc_restore_charset();
    return input_wait_next();
}
