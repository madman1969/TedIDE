/*
 * Tedide sample: a scrolling world map far bigger than the C64's memory, kept in far memory
 * with the FarMem library (../FarMem). With a 512K REU the map is 512x256 cells - 128K, twice
 * the machine's whole address space; with less far memory it shrinks to fit.
 *
 * Joystick (port 2) or cursor keys scroll the map; hold fire, or use W/A/S/D, to move faster.
 * T, F and G change the cell under the cursor to a town, forest or grass, M shows the whole
 * world, N makes a new one and Q quits.
 *
 * Split into modules:
 *   world.c/.h     - generates the map and reads and writes its cells in far memory
 *   terrain.s/.h   - the generator's two per-row loops, in assembly for speed
 *   view.c/.h      - draws the map, the status bar and the overview
 *   blit.s         - draws the map view onto the screen without tearing, in assembly
 *   charset.c/.h   - the hand-drawn tile characters, and moving the screen to show them
 */
#include <stdlib.h>
#include <conio.h>
#include <cbm.h>
#include <joystick.h>
#include "farmem.h"
#include "world.h"
#include "view.h"

#define FAST_STEP  8

/* The view's top-left cell. */
static unsigned left;
static unsigned top;

/* One whole map row, for the overview. */
static unsigned char map_row[512];

/* Moves pos by delta, keeping it between 0 and max. */
static unsigned move(unsigned pos, int delta, unsigned max)
{
    if (delta < 0)
        return (unsigned)-delta > pos ? 0 : pos + delta;
    pos += delta;
    return pos > max ? max : pos;
}

/* Called by world_generate as each row is finished: adds it to the overview, and updates the
 * percentage now and then. */
static void row_generated(unsigned y, const unsigned char* tiles)
{
    view_overview_row(y, tiles);
    if ((y & 7) == 0)
        view_progress((unsigned char)((unsigned long)y * 100 / world_height()));
}

/* Generates a new world, showing it take shape, and centres the view on it. */
static void new_world(void)
{
    view_overview_begin("generating a new world...");
    world_generate(row_generated);
    left = (world_width() - VIEW_WIDTH) / 2;
    top = (world_height() - VIEW_HEIGHT) / 2;
    clrscr();
    view_invalidate();
}

/* Shows the whole world, read back from far memory a row at a time, with the cursor's place
 * marked, until a key is pressed. */
static void show_overview(void)
{
    unsigned y;

    view_overview_begin("the whole world - press a key");
    for (y = 0; y < world_height(); y += view_overview_step())
    {
        world_read(0, y, map_row, world_width());
        view_overview_row(y, map_row);
    }
    view_overview_mark(left + CURSOR_X, top + CURSOR_Y);
    cgetc();
    clrscr();
    view_invalidate();
}

/* Changes the cell under the cursor. Editing the map is a far_poke through FarMem's window
 * cache; the view's own cache of the map then has to be refilled. */
static void set_cursor_tile(unsigned char tile)
{
    world_set(left + CURSOR_X, top + CURSOR_Y, tile);
    view_invalidate();
}

/* Redraws the map and the status bar for the cell under the cursor. */
static void redraw(void)
{
    view_status(left + CURSOR_X, top + CURSOR_Y, view_draw(left, top));
}

int main(void)
{
    unsigned char joy, key, step, quit = 0;
    int dx, dy;
    unsigned char changed;

    if (far_init() == FAR_NONE || !world_create())
    {
        cputs("not enough memory for a world map.\r\n");
        return 1;
    }
    view_init();
    joy_install(joy_static_stddrv);

    _randomize();   /* seed rand() from the clock, for a different world each run */
    new_world();
    redraw();

    while (!quit)
    {
        dx = dy = 0;
        changed = 0;

        /* Joystick: one cell per frame, or four with fire held. */
        joy = joy_read(JOY_2);
        step = JOY_BTN_1(joy) ? 4 : 1;
        if (JOY_UP(joy))    dy = -step;
        if (JOY_DOWN(joy))  dy = step;
        if (JOY_LEFT(joy))  dx = -step;
        if (JOY_RIGHT(joy)) dx = step;

        if (kbhit())
        {
            key = cgetc();
            switch (key)
            {
            case CH_CURS_UP:    dy = -1; break;
            case CH_CURS_DOWN:  dy = 1; break;
            case CH_CURS_LEFT:  dx = -1; break;
            case CH_CURS_RIGHT: dx = 1; break;
            case 'w': dy = -FAST_STEP; break;
            case 's': dy = FAST_STEP; break;
            case 'a': dx = -FAST_STEP; break;
            case 'd': dx = FAST_STEP; break;
            case 't': set_cursor_tile(TILE_TOWN); changed = 1; break;
            case 'f': set_cursor_tile(TILE_FOREST); changed = 1; break;
            case 'g': set_cursor_tile(TILE_GRASS); changed = 1; break;
            case 'm': show_overview(); changed = 1; break;
            case 'n': new_world(); changed = 1; break;
            case 'q': quit = 1; break;
            }
        }

        if (dx != 0 || dy != 0)
        {
            left = move(left, dx, world_width() - VIEW_WIDTH);
            top = move(top, dy, world_height() - VIEW_HEIGHT);
            changed = 1;
        }
        if (changed)
            redraw();
        else
            waitvsync();    /* nothing to do: poll again next frame */
    }

    joy_uninstall();
    far_done();
    view_done();
    return 0;
}
