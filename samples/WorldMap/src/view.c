#include <string.h>
#include <conio.h>
#include <ctype.h>
#include "farmem.h"
#include "screen.h"
#include "world.h"
#include "view.h"

/* The text screen and its colours, which conio and the overviews use (screen.h). The map is
 * drawn through screen_draw_chars/colours instead, as on the Plus/4 that's a hidden screen. */
#define SCREEN       ((unsigned char*)SCREEN_CHARS)
#define COLOURS      ((unsigned char*)SCREEN_COLOURS)

#define OVERVIEW_TOP   3
#define OVERVIEW_ROWS  16

/* The character for each tile: its picture's, drawn by screen_c64.c or screen_plus4.c. Filled
 * in by view_init. Not static: blit.s reads it, and the tiles' colours (view_tile_colour, in
 * those files) too. */
unsigned char view_tile_char[TILE_COUNT];

/* The status bar's name for each picture - every shade of a kind has the same one. */
static const char* const glyph_name[GLYPH_TOWN + 1] = {
    "deep sea", "sea", "beach", "grass", "forest", "hills", "mountain", "snow", "town",
};

#define CURSOR_CHAR    (SCREEN_FIRST_GLYPH + GLYPH_CURSOR)
#define CURSOR_OFFSET  ((SCREEN_MAP_TOP + CURSOR_Y) * SCREEN_COLS + CURSOR_X)

/* Where each view row's 40 tiles are, split into low and high bytes for blit.s, which draws
 * them (see there). */
unsigned char view_row_lo[VIEW_HEIGHT];
unsigned char view_row_hi[VIEW_HEIGHT];
void view_blit(void);

/* The tile cache: VIEW_HEIGHT map rows, cache_w columns wide starting at column cache_left.
 * Map row y lives in cache[y % VIEW_HEIGHT], so the rows on view always fill the slots;
 * scrolling up or down a row reads just the row that comes into view, over the one that
 * left. Scrolling sideways reads nothing until the view nears the cache's edge - then the
 * cache is re-centred on the view and refilled. */
#define CACHE_MAX_W  128
static unsigned char cache[VIEW_HEIGHT][CACHE_MAX_W];
static unsigned cache_w;
static unsigned cache_left;
static unsigned cache_top;
static unsigned char cache_valid;

/* Whether the status bar needs setting up again, after the screen was cleared. */
static unsigned char status_dirty;

/* The top-left cell of the frame drawn last, to tell a move within it from one out of it, and
 * the cell its cursor is over. */
static unsigned drawn_left;
static unsigned drawn_top;
static unsigned cursor_x;
static unsigned cursor_y;

static char backend_name[13];

/* The map column each overview column samples. */
static unsigned overview_x[SCREEN_COLS];

/* Puts one tile's character and colour on the text screen. */
static void put_tile(unsigned char x, unsigned char y, unsigned char tile)
{
    unsigned offset = y * SCREEN_COLS + x;
    SCREEN[offset] = view_tile_char[tile];
    COLOURS[offset] = view_tile_colour[tile];
}

void view_init(void)
{
    unsigned char i;
    const char* name = far_backend_name();

    bordercolor(COLOR_BLACK);
    bgcolor(COLOR_BLACK);
    cursor(0);
    screen_init();      /* also clears the screen */

    for (i = 0; i < TILE_COUNT; ++i)
        view_tile_char[i] = SCREEN_FIRST_GLYPH + tile_glyph[i];

    /* In the C64's and Plus/4's uppercase character set, a capital letter in a C string shows
     * as a graphics character, so the backend name ("REU") is shown in lowercase. */
    for (i = 0; i < sizeof(backend_name) - 1 && name[i]; ++i)
        backend_name[i] = tolower(name[i]);
    backend_name[i] = 0;

    cache_w = world_width() < CACHE_MAX_W ? world_width() : CACHE_MAX_W;
    view_invalidate();
}

void view_done(void)
{
    screen_done();
    textcolor(COLOR_LIGHTBLUE);
    bordercolor(COLOR_LIGHTBLUE);
    bgcolor(COLOR_BLUE);
    clrscr();
    cursor(1);
}

void view_invalidate(void)
{
    cache_valid = 0;
    status_dirty = 1;
}

/* Reads map row y's cached columns from far memory into its cache slot. */
static void load_row(unsigned y)
{
    screen_before_far_read();
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

/* Writes text straight into the screen being drawn, padded with spaces to len characters.
 * Letters in a C string are PETSCII, which the screen shows 64 codes lower. */
static void put_text(unsigned offset, const char* text, unsigned char len)
{
    unsigned char i, c;
    for (i = 0; i < len; ++i)
    {
        c = *text ? *text++ : ' ';
        screen_draw_chars[offset + i] = (c >= 0x40 && c < 0x60) ? c - 0x40 : c;
    }
}

/* Writes n (0-999) straight into the screen being drawn, right-aligned in three characters. */
static void put_number(unsigned offset, unsigned n)
{
    screen_draw_chars[offset] = n >= 100 ? '0' + n / 100 : ' ';
    screen_draw_chars[offset + 1] = n >= 10 ? '0' + n / 10 % 10 : ' ';
    screen_draw_chars[offset + 2] = '0' + n % 10;
}

/* The status bar's top line - "reu          x123 y 45 grass     m:map" - and its keys below. */
#define STATUS_LINE  (SCREEN_STATUS_ROW * SCREEN_COLS)

/* Writes the status bar, for the cell (x, y) under the cursor and its tile, into the screen
 * being drawn. All of it each time: on the Plus/4 it goes to each of two screens in turn. */
static void draw_status(unsigned x, unsigned y, unsigned char tile)
{
    put_text(STATUS_LINE, backend_name, 13);
    put_text(STATUS_LINE + 13, "x", 1);
    put_number(STATUS_LINE + 14, x);
    put_text(STATUS_LINE + 17, " y", 2);
    put_number(STATUS_LINE + 19, y);
    put_text(STATUS_LINE + 22, " ", 1);
    put_text(STATUS_LINE + 23, glyph_name[tile_glyph[tile]], 8);
    put_text(STATUS_LINE + 31, "  m:map", 9);
    put_text(STATUS_LINE + SCREEN_COLS, "t:town f:forest g:grass n:new q:quit", SCREEN_COLS);
}

void view_draw(unsigned left, unsigned top)
{
    unsigned char row, slot, tile;
    unsigned char fine_x = left & ((1 << VIEW_FINE_BITS) - 1);
    unsigned char fine_y = top & ((1 << VIEW_FINE_BITS) - 1);
    unsigned cell_left = VIEW_CELL(left);
    /* The map row drawn at the top of the screen - see SCREEN_ROW_SHIFT. */
    unsigned cell_top = VIEW_CELL(top) + SCREEN_ROW_SHIFT(fine_y);
    const unsigned char* tiles;

    /* Still the same top-left cell: on the Plus/4, scroll the frame on show and that's all. */
    if (cache_valid && !status_dirty && cell_left == drawn_left && cell_top == drawn_top)
    {
        screen_scroll(fine_x, fine_y);
        return;
    }

    if (status_dirty)
    {
        /* Back from an overview or a cleared screen: the map screen, with the status bar's
         * colours set. */
        screen_map();
        memset(screen_draw_colours + STATUS_LINE, COLOR_GRAY3, 2 * SCREEN_COLS);
        status_dirty = 0;
    }

    update_cache(cell_left, cell_top);

    /* Point blit.s at each view row's tiles in the cache. */
    slot = cell_top % VIEW_HEIGHT;
    for (row = 0; row < VIEW_HEIGHT; ++row)
    {
        tiles = cache[slot] + (cell_left - cache_left);
        view_row_lo[row] = (unsigned char)tiles;
        view_row_hi[row] = (unsigned char)((unsigned)tiles >> 8);
        if (++slot == VIEW_HEIGHT)
            slot = 0;
    }
    view_blit();

    screen_draw_chars[CURSOR_OFFSET] = CURSOR_CHAR;
    screen_draw_colours[CURSOR_OFFSET] = screen_cursor_colour;

    cursor_x = cell_left + CURSOR_X;
    cursor_y = cell_top + CURSOR_Y;
    tile = cache[cursor_y % VIEW_HEIGHT][cursor_x - cache_left];
    draw_status(cursor_x, cursor_y, tile);

    screen_present(fine_x, fine_y);
    drawn_left = cell_left;
    drawn_top = cell_top;
}

unsigned view_cursor_x(void)
{
    return cursor_x;
}

unsigned view_cursor_y(void)
{
    return cursor_y;
}

void view_overview_begin(const char* title)
{
    unsigned char x;

    screen_text();
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
    COLOURS[offset] = screen_cursor_colour;
}

void view_progress(unsigned char percent)
{
    textcolor(COLOR_GRAY3);
    gotoxy(0, OVERVIEW_TOP + OVERVIEW_ROWS + 1);
    cprintf("%3u%%", percent);
}
