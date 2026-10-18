#include <string.h>
#include <conio.h>
#include <c64.h>
#include "farmem.h"
#include "screen.h"

/* The VIC-II can only read a character set from the 16K video bank it's showing, and in the
 * C64's default bank ($0000-$3FFF) the free 2K slots are taken by this program's code. So the
 * screen and character set move to bank 3, into the RAM under the KERNAL ROM: the CPU writes
 * there straight through the ROM, cc65 never uses it, and the video chip always sees RAM. */

/* The C64 has few shades of each colour, so most shades of a kind share one. */
const unsigned char view_tile_colour[TILE_COUNT] = {
    COLOR_BLUE, COLOR_BLUE, COLOR_BLUE,         /* deepest, deeper, deep */
    COLOR_LIGHTBLUE, COLOR_CYAN,                /* sea, shallows */
    COLOR_YELLOW,                               /* beach */
    COLOR_LIGHTGREEN, COLOR_GREEN,              /* grass, upland */
    COLOR_GREEN, COLOR_GREEN,                   /* forest, high forest */
    COLOR_BROWN, COLOR_BROWN,                   /* hills, high hills */
    COLOR_GRAY1, COLOR_GRAY2,                   /* mountain, high mountain */
    COLOR_WHITE,                                /* snow */
    COLOR_LIGHTRED,                             /* town */
    COLOR_LIGHTBLUE,                            /* river */
};
const unsigned char screen_cursor_colour = COLOR_WHITE;

/* The C64 draws each frame straight onto the screen, racing the raster beam (blit.s). */
unsigned char* screen_draw_chars = (unsigned char*)SCREEN_CHARS;
unsigned char* screen_draw_colours = (unsigned char*)SCREEN_COLOURS;

#define CHARSET      0xE800
#define CHARS        ((unsigned char*)CHARSET)
#define CHAR_ROM     ((unsigned char*)0xD000)
#define CPU_PORT     (*(unsigned char*)0x0001)
#define HIBASE       (*(unsigned char*)0x0288)   /* KERNAL: the screen's page */
#define CASE_LOCK    (*(unsigned char*)0x0291)   /* KERNAL: $80 stops C=+Shift changing case */

/* The VIC-II's memory pointers for bank 3: screen at $E000 (bank offset $2000, 8 x 1K) and
 * characters at $E800 (offset $2800, 5 x 2K). */
#define VIC_ADDR_BANK3  ((((SCREEN_CHARS - 0xC000) / 0x400) << 4) | (((CHARSET - 0xC000) / 0x800) << 1))

/* SuperCPU registers: by default it only copies writes to the usual screen at $0400 through to
 * the C64's own RAM, which the video chip reads, so it has to be told to copy all of them. */
#define SCPU_ENABLE_REGS   (*(unsigned char*)0xD07E)
#define SCPU_MIRROR_ALL    (*(unsigned char*)0xD077)
#define SCPU_DISABLE_REGS  (*(unsigned char*)0xD07F)

static unsigned char saved_vic_addr;

void screen_init(void)
{
    unsigned char port;

    if (far_backend() == FAR_SCPU)
    {
        SCPU_ENABLE_REGS = 0;
        SCPU_MIRROR_ALL = 0;
        SCPU_DISABLE_REGS = 0;
    }

    /* The character ROM is only visible to the CPU with the I/O area switched out, so
     * interrupts (whose handler needs I/O) are off while it's copied. */
    __asm__("sei");
    port = CPU_PORT;
    CPU_PORT = port & ~0x04;
    memcpy(CHARS, CHAR_ROM, 0x800);
    CPU_PORT = port;
    __asm__("cli");

    memcpy(CHARS + SCREEN_FIRST_GLYPH * 8, tile_glyphs, sizeof(tile_glyphs));

    /* Clear the new screen before the video chip shows it: it's whatever was in that RAM. The
     * KERNAL - and so conio - finds the screen through HIBASE. */
    HIBASE = SCREEN_CHARS >> 8;
    CASE_LOCK = 0x80;
    clrscr();

    saved_vic_addr = VIC.addr;
    VIC.addr = VIC_ADDR_BANK3;
    CIA2.pra &= 0xFC;           /* bank 3: the bank bits are inverted */
}

void screen_done(void)
{
    CIA2.pra |= 0x03;           /* bank 0 */
    VIC.addr = saved_vic_addr;
    HIBASE = 0x04;
    CASE_LOCK = 0;
}

/* The C64 has one screen and no fine scrolling, so the text and map screens are the same and a
 * drawn frame is already on show. */
void screen_text(void)
{
}

void screen_map(void)
{
}

void screen_present(unsigned char fine_x, unsigned char fine_y)
{
    (void)fine_x;
    (void)fine_y;
}

void screen_scroll(unsigned char fine_x, unsigned char fine_y)
{
    (void)fine_x;
    (void)fine_y;
}

unsigned char screen_far_read_lines(void)
{
    return 255;
}

void screen_set_copy_wait_work(void (*work)(void))
{
    (void)work;
}

unsigned char screen_time_for(unsigned char lines)
{
    (void)lines;
    return 0;
}
