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
 * block, 0x66 a checkerboard, 0x58 a club (a tree) and 0x51 a filled circle. Not static:
 * blit.s reads them too. */
const unsigned char view_tile_char[TILE_COUNT] = {
    0xA0, 0x66, 0xA0, 0xA0, 0x58, 0x66, 0xA0, 0xA0, 0x51,
};
const unsigned char view_tile_colour[TILE_COUNT] = {
    COLOR_BLUE, COLOR_LIGHTBLUE, COLOR_YELLOW, COLOR_GREEN, COLOR_GREEN,
    COLOR_BROWN, COLOR_GRAY2, COLOR_WHITE, COLOR_LIGHTRED,
};
static const char* const tile_name[TILE_COUNT] = {
    "deep sea", "sea", "beach", "grass", "forest", "hills", "mountain", "snow", "town",
};

#define CURSOR_CHAR    0x56   /* a diagonal cross */
#define CURSOR_OFFSET  (CURSOR_Y * SCREEN_COLS + CURSOR_X)

/* Where each view row's 40 tiles are, split into low and high bytes for blit.s, which draws
 * them (see there). */
unsigned char view_row_lo[VIEW_HEIGHT];
unsigned char view_row_hi[VIEW_HEIGHT];
void view_blit(void);

/* The tile cache: VIEW_HEIGHT map rows, cache_w columns wide starting at column cache_left.
 * Map row y lives in cache[y % VIEW_HEIGHT], so the 23 rows on view always fill the 23 slots;
 * scrolling up or down a row reads just the row that comes into view, over the one that
 * left. Scrolling sideways reads nothing until the view nears the cache's edge - then the
 * cache is re-centred on the view and refilled. */
#define CACHE_MAX_W  128
static unsigned char cache[VIEW_HEIGHT][CACHE_MAX_W];
static unsigned cache_w;
static unsigned cache_left;
static unsigned cache_top;
static unsigned char cache_valid;

/* Whether the status bar's fixed text needs drawing again. */
static unsigned char status_dirty;

static char backend_name[13];

/* The map column each overview column samples. */
static unsigned overview_x[SCREEN_COLS];

/* Puts one tile's character and colour at a screen position. */
static void put_tile(unsigned char x, unsigned char y, unsigned char tile)
{
    unsigned offset = y * SCREEN_COLS + x;
    SCREEN[offset] = view_tile_char[tile];
    COLOR_RAM[offset] = view_tile_colour[tile];
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

    cache_w = world_width() < CACHE_MAX_W ? world_width() : CACHE_MAX_W;
    view_invalidate();
}

void view_invalidate(void)
{
    cache_valid = 0;
    status_dirty = 1;
}

/* Reads map row y's cached columns from far memory into its cache slot. */
static void load_row(unsigned y)
{
    world_read(cache_left, y, cache[y % VIEW_HEIGHT], cache_w);
}

/* Brings the cache into line with a view whose top-left cell is (left, top). */
static void update_cache(unsigned left, unsigned top)
{
    unsigned y, first, end;

    if (!cache_valid || left < cache_left || left + VIEW_WIDTH > cache_left + cache_w)
    {
        /* Centre the cache on the view, as far as the map's edges allow, and fill it. */
        cache_left = left > (cache_w - VIEW_WIDTH) / 2 ? left - (cache_w - VIEW_WIDTH) / 2 : 0;
        if (cache_left + cache_w > world_width())
            cache_left = world_width() - cache_w;
        for (y = top; y < top + VIEW_HEIGHT; ++y)
            load_row(y);
    }
    else if (top > cache_top)
    {
        /* Scrolled down: read the rows below the old view. */
        first = top > cache_top + VIEW_HEIGHT ? top : cache_top + VIEW_HEIGHT;
        for (y = first; y < top + VIEW_HEIGHT; ++y)
            load_row(y);
    }
    else if (top < cache_top)
    {
        /* Scrolled up: read the rows above it. */
        end = top + VIEW_HEIGHT < cache_top ? top + VIEW_HEIGHT : cache_top;
        for (y = top; y < end; ++y)
            load_row(y);
    }
    cache_top = top;
    cache_valid = 1;
}

unsigned char view_draw(unsigned left, unsigned top)
{
    unsigned char row, slot;
    const unsigned char* tiles;

    update_cache(left, top);

    /* Point blit.s at each view row's tiles in the cache. */
    slot = top % VIEW_HEIGHT;
    for (row = 0; row < VIEW_HEIGHT; ++row)
    {
        tiles = cache[slot] + (left - cache_left);
        view_row_lo[row] = (unsigned char)tiles;
        view_row_hi[row] = (unsigned char)((unsigned)tiles >> 8);
        if (++slot == VIEW_HEIGHT)
            slot = 0;
    }
    view_blit();

    SCREEN[CURSOR_OFFSET] = CURSOR_CHAR;
    COLOR_RAM[CURSOR_OFFSET] = COLOR_WHITE;

    return cache[(top + CURSOR_Y) % VIEW_HEIGHT][left + CURSOR_X - cache_left];
}

/* Writes text straight into screen memory, padded with spaces to len characters. Letters in a
 * C string are PETSCII, which the screen shows 64 codes lower. */
static void put_text(unsigned offset, const char* text, unsigned char len)
{
    unsigned char i, c;
    for (i = 0; i < len; ++i)
    {
        c = *text ? *text++ : ' ';
        SCREEN[offset + i] = (c >= 0x40 && c < 0x60) ? c - 0x40 : c;
    }
}

/* Writes n (0-999) straight into screen memory, right-aligned in three characters. */
static void put_number(unsigned offset, unsigned n)
{
    SCREEN[offset] = n >= 100 ? '0' + n / 100 : ' ';
    SCREEN[offset + 1] = n >= 10 ? '0' + n / 10 % 10 : ' ';
    SCREEN[offset + 2] = '0' + n % 10;
}

/* The status bar's top line, and where its changing parts are in it. */
#define STATUS_LINE     (VIEW_HEIGHT * SCREEN_COLS)
#define STATUS_X        (STATUS_LINE + 14)
#define STATUS_Y        (STATUS_LINE + 19)
#define STATUS_TILE     (STATUS_LINE + 23)

void view_status(unsigned x, unsigned y, unsigned char tile)
{
    if (status_dirty)
    {
        /* The fixed text, once; cprintf is far too slow to redo on every step. Both lines stop
         * short of the last column: printing in the bottom-right corner scrolls the screen. */
        textcolor(COLOR_GRAY3);
        gotoxy(0, VIEW_HEIGHT);
        cprintf("%-12s x    y             m:map", backend_name);
        gotoxy(0, VIEW_HEIGHT + 1);
        cprintf("t:town f:forest g:grass n:new q:quit");
        status_dirty = 0;
    }
    put_number(STATUS_X, x);
    put_number(STATUS_Y, y);
    put_text(STATUS_TILE, tile_name[tile], 8);
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
