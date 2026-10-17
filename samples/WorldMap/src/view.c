#include <conio.h>
#include <ctype.h>
#include <c64.h>
#include "farmem.h"
#include "world.h"
#include "view.h"

#define SCREEN       ((unsigned char*)0x0400)
#define SCREEN_COLS  40

#define OVERVIEW_TOP   3
#define OVERVIEW_ROWS  16

/* Screen codes (uppercase/graphics character set) and colours for each tile. 0xA0 is a solid
 * block, 0x66 a checkerboard, 0x58 a club (a tree) and 0x51 a filled circle. */
static const unsigned char tile_char[TILE_COUNT] = {
    0xA0, 0x66, 0xA0, 0xA0, 0x58, 0x66, 0xA0, 0xA0, 0x51,
};
static const unsigned char tile_colour[TILE_COUNT] = {
    COLOR_BLUE, COLOR_LIGHTBLUE, COLOR_YELLOW, COLOR_GREEN, COLOR_GREEN,
    COLOR_BROWN, COLOR_GRAY2, COLOR_WHITE, COLOR_LIGHTRED,
};
static const char* const tile_name[TILE_COUNT] = {
    "deep sea", "sea", "beach", "grass", "forest", "hills", "mountain", "snow", "town",
};

#define CURSOR_CHAR  0x56   /* a diagonal cross */

static unsigned char row[VIEW_WIDTH];
static char backend_name[13];

/* The map column each overview column samples. */
static unsigned overview_x[SCREEN_COLS];

/* Puts one tile's character and colour at a screen position. */
static void put_tile(unsigned char x, unsigned char y, unsigned char tile)
{
    unsigned offset = y * SCREEN_COLS + x;
    SCREEN[offset] = tile_char[tile];
    COLOR_RAM[offset] = tile_colour[tile];
}

void view_init(void)
{
    unsigned char i;
    const char* name = far_backend_name();

    bordercolor(COLOR_BLACK);
    bgcolor(COLOR_BLACK);
    cursor(0);
    clrscr();

    /* In the C64's uppercase character set, a capital letter in a C string shows as a
     * graphics character, so the backend name ("REU") is shown in lowercase. */
    for (i = 0; i < sizeof(backend_name) - 1 && name[i]; ++i)
        backend_name[i] = tolower(name[i]);
    backend_name[i] = 0;
}

void view_draw(unsigned left, unsigned top)
{
    unsigned char x, y;
    unsigned char* screen = SCREEN;
    unsigned char* colour = COLOR_RAM;

    for (y = 0; y < VIEW_HEIGHT; ++y)
    {
        /* One far_read fetches the whole row of tiles; the table lookups turn each into a
         * character and a colour. */
        world_read(left, top + y, row, VIEW_WIDTH);
        for (x = 0; x < VIEW_WIDTH; ++x)
        {
            screen[x] = tile_char[row[x]];
            colour[x] = tile_colour[row[x]];
        }
        screen += SCREEN_COLS;
        colour += SCREEN_COLS;
    }

    SCREEN[CURSOR_Y * SCREEN_COLS + CURSOR_X] = CURSOR_CHAR;
    COLOR_RAM[CURSOR_Y * SCREEN_COLS + CURSOR_X] = COLOR_WHITE;
}

void view_status(unsigned x, unsigned y, unsigned char tile)
{
    textcolor(COLOR_GRAY3);
    /* Padded with spaces to a fixed width, so shorter values overwrite longer ones. Both lines
     * stop short of the last column: printing in the bottom-right corner scrolls the screen. */
    gotoxy(0, VIEW_HEIGHT);
    cprintf("%-12s x%3u y%3u %-8s  m:map", backend_name, x, y, tile_name[tile]);
    gotoxy(0, VIEW_HEIGHT + 1);
    cprintf("t:town f:forest g:grass n:new q:quit");
}

void view_overview_begin(const char* title)
{
    unsigned char x;

    clrscr();
    textcolor(COLOR_GRAY3);
    cputsxy(0, 0, title);
    gotoxy(0, 1);
    cprintf("%ux%u cells in %s, %lu bytes", world_width(), world_height(), backend_name, far_size());

    for (x = 0; x < SCREEN_COLS; ++x)
        overview_x[x] = (unsigned)((unsigned long)x * world_width() / SCREEN_COLS);
}

unsigned view_overview_step(void)
{
    return world_height() / OVERVIEW_ROWS;
}

void view_overview_row(unsigned y, const unsigned char* tiles)
{
    unsigned char x;
    unsigned char screen_y;

    if (y % view_overview_step() != 0)
        return;
    screen_y = OVERVIEW_TOP + y / view_overview_step();
    for (x = 0; x < SCREEN_COLS; ++x)
        put_tile(x, screen_y, tiles[overview_x[x]]);
}

void view_overview_mark(unsigned x, unsigned y)
{
    unsigned offset = (OVERVIEW_TOP + y / view_overview_step()) * SCREEN_COLS
        + (unsigned)((unsigned long)x * SCREEN_COLS / world_width());
    SCREEN[offset] = CURSOR_CHAR;
    COLOR_RAM[offset] = COLOR_WHITE;
}

void view_progress(unsigned char percent)
{
    textcolor(COLOR_GRAY3);
    gotoxy(0, OVERVIEW_TOP + OVERVIEW_ROWS + 1);
    cprintf("%3u%%", percent);
}
