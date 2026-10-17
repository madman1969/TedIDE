#include <stdlib.h>
#include "farmem.h"
#include "terrain.h"
#include "world.h"

/* The terrain is two layers of "value noise" added together: a coarse grid of random heights
 * every TERRAIN_SPAN_A (32) cells for the continents, and a finer one every TERRAIN_SPAN_B (8)
 * cells for detail, each smoothly interpolated between its grid points, plus a little per-cell
 * roughness. The total height then picks the tile, from deep sea up to snow.
 *
 * The map is built a row at a time. For each band of grid rows, every grid column gets a
 * height and a per-row step towards the grid row below (start_band_a/b); terrain_row() then
 * interpolates across each row, and terrain_next_row() steps every column down a row. Those
 * two run for every row, so they're in assembly - see terrain.s. */

#define MAX_HEIGHT  256
#define GRID_A_H    (MAX_HEIGHT / TERRAIN_SPAN_A + 1)
#define GRID_B_H    (MAX_HEIGHT / TERRAIN_SPAN_B + 1)

/* The map sizes to try, biggest first. Each is a power of two (so a random column is a quick
 * mask), a multiple of TERRAIN_SPAN_A both ways, and at least the 40x23 view. */
static const unsigned sizes[][2] = {
    { 512, 256 }, { 256, 256 }, { 256, 128 }, { 128, 64 }, { 64, 32 },
};
#define SIZE_COUNT (sizeof(sizes) / sizeof(sizes[0]))

/* Upper height limits for deep sea, sea, beach, grass, forest, hills and mountain; anything
 * higher is snow. Tuned so roughly half the map is sea. */
static const unsigned char thresholds[] = { 70, 82, 87, 108, 122, 133, 144 };

/* How many random cells per row may become a town, if they're grassland. */
#define TOWN_TRIES_PER_ROW  2

/* The tables shared with terrain.s - see terrain.h. */
unsigned char terrain_col_a_lo[TERRAIN_GRID_A_W];
unsigned char terrain_col_a_hi[TERRAIN_GRID_A_W];
unsigned char terrain_step_a_lo[TERRAIN_GRID_A_W];
unsigned char terrain_step_a_hi[TERRAIN_GRID_A_W];
unsigned char terrain_col_b_lo[TERRAIN_GRID_B_W];
unsigned char terrain_col_b_hi[TERRAIN_GRID_B_W];
unsigned char terrain_step_b_lo[TERRAIN_GRID_B_W];
unsigned char terrain_step_b_hi[TERRAIN_GRID_B_W];
unsigned char terrain_columns_a;
unsigned char terrain_columns_b;
unsigned char terrain_roughness[256];
unsigned char terrain_roughness_start;
unsigned char terrain_tile_for_height[256];
unsigned char terrain_row_buf[TERRAIN_MAX_WIDTH];

static farptr cells;
static unsigned width;
static unsigned height;

/* Grid heights: 0-127 for the coarse layer and 0-63 for the fine one, so that in 8.8 fixed
 * point every difference between two of them fits in a 16-bit int. */
static unsigned char grid_a[GRID_A_H][TERRAIN_GRID_A_W];
static unsigned char grid_b[GRID_B_H][TERRAIN_GRID_B_W];

/* The address of cell (x, y) in far memory. */
static farptr cell(unsigned x, unsigned y)
{
    return cells + (unsigned long)y * width + x;
}

unsigned char world_create(void)
{
    unsigned char i;
    unsigned h;
    unsigned char t;

    for (i = 0; i < SIZE_COUNT; ++i)
    {
        if ((unsigned long)sizes[i][0] * sizes[i][1] <= far_maxavail())
        {
            cells = far_alloc((unsigned long)sizes[i][0] * sizes[i][1]);
            if (cells != FAR_NULL)
            {
                width = sizes[i][0];
                height = sizes[i][1];
                break;
            }
        }
    }
    if (cells == FAR_NULL)
        return 0;

    terrain_columns_a = width / TERRAIN_SPAN_A + 1;
    terrain_columns_b = width / TERRAIN_SPAN_B + 1;

    /* A lookup table from height to tile, so each cell is one table read. Heights of 240 and
     * up only happen when the roughness pushes a height below 0 and it wraps round, so
     * they're deep sea too. */
    for (h = 0; h < 256; ++h)
    {
        if (h >= 240)
            t = TILE_DEEP;
        else
            for (t = 0; t < sizeof(thresholds) && h >= thresholds[t]; ++t)
                ;
        terrain_tile_for_height[h] = t;
    }
    return 1;
}

unsigned world_width(void)
{
    return width;
}

unsigned world_height(void)
{
    return height;
}

/* Loop state for the per-band work below, static rather than on cc65's software stack, which
 * is several times slower to reach. */
static unsigned char col;
static int step;
static const unsigned char* above;
static const unsigned char* below;

/* Starts a new band of coarse grid rows: each column's height is set to grid row cy's, and its
 * per-row step to reach grid row cy + 1 after TERRAIN_SPAN_A rows. The two grid rows are walked
 * with pointers, as indexing a 2D array multiplies on every access. */
static void start_band_a(unsigned char cy)
{
    above = grid_a[cy];
    below = grid_a[cy + 1];
    for (col = 0; col < terrain_columns_a; ++col)
    {
        terrain_col_a_lo[col] = 0;
        terrain_col_a_hi[col] = above[col];
        step = ((int)below[col] - above[col]) << 3;     /* x 256 / TERRAIN_SPAN_A */
        terrain_step_a_lo[col] = (unsigned char)step;
        terrain_step_a_hi[col] = (unsigned char)(step >> 8);
    }
}

/* The same for the fine grid, every TERRAIN_SPAN_B rows. */
static void start_band_b(unsigned char cy)
{
    above = grid_b[cy];
    below = grid_b[cy + 1];
    for (col = 0; col < terrain_columns_b; ++col)
    {
        terrain_col_b_lo[col] = 0;
        terrain_col_b_hi[col] = above[col];
        step = ((int)below[col] - above[col]) << 5;     /* x 256 / TERRAIN_SPAN_B */
        terrain_step_b_lo[col] = (unsigned char)step;
        terrain_step_b_hi[col] = (unsigned char)(step >> 8);
    }
}

/* Turns a couple of random cells in the row just built into towns, if they're grassland -
 * about a hundred towns on the biggest map. */
static void place_towns(void)
{
    unsigned char i;
    unsigned x;

    for (i = 0; i < TOWN_TRIES_PER_ROW; ++i)
    {
        x = rand() & (width - 1);
        if (terrain_row_buf[x] == TILE_GRASS)
            terrain_row_buf[x] = TILE_TOWN;
    }
}

void world_generate(void (*row_done)(unsigned y, const unsigned char* tiles))
{
    unsigned char grid_a_h = height / TERRAIN_SPAN_A + 1;
    unsigned char grid_b_h = height / TERRAIN_SPAN_B + 1;
    unsigned char cx, cy;
    unsigned y;
    farptr row = cells;

    /* Random grid heights. The coarse grid's edges are 0, so the map is ringed by sea. */
    for (cy = 0; cy < grid_a_h; ++cy)
        for (cx = 0; cx < terrain_columns_a; ++cx)
            grid_a[cy][cx] = (cx == 0 || cy == 0 || cx == terrain_columns_a - 1 || cy == grid_a_h - 1)
                ? 0 : rand() & 127;
    for (cy = 0; cy < grid_b_h; ++cy)
        for (cx = 0; cx < terrain_columns_b; ++cx)
            grid_b[cy][cx] = rand() & 63;

    for (cx = 0; ; ++cx)
    {
        terrain_roughness[cx] = (unsigned char)((rand() & 7) - 4);
        if (cx == 255)
            break;
    }

    /* One row at a time: built in terrain_row_buf, then written to far memory in one go. */
    for (y = 0; y < height; ++y)
    {
        if ((y & (TERRAIN_SPAN_A - 1)) == 0)
            start_band_a(y / TERRAIN_SPAN_A);
        if ((y & (TERRAIN_SPAN_B - 1)) == 0)
            start_band_b(y / TERRAIN_SPAN_B);

        /* Each row starts at a different place in the roughness table, so rows don't repeat it. */
        terrain_roughness_start = (unsigned char)rand();
        terrain_row();
        place_towns();
        far_write(row, terrain_row_buf, width);
        row += width;
        terrain_next_row();

        row_done(y, terrain_row_buf);
    }
}

void world_read(unsigned x, unsigned y, unsigned char* dst, unsigned len)
{
    far_read(dst, cell(x, y), len);
}

unsigned char world_get(unsigned x, unsigned y)
{
    return far_peek(cell(x, y));
}

void world_set(unsigned x, unsigned y, unsigned char tile)
{
    far_poke(cell(x, y), tile);
}
