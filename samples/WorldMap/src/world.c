#include <stdlib.h>
#include <string.h>
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

/* Upper height limits for each tile from TILE_DEEPEST to TILE_HIGH_MOUNTAIN; anything
 * higher is snow. Tuned from the heights of generated worlds (half of them are below 81, a tenth
 * below 40, a tenth above 124) so that about 30% of the map is sea, with each kind of land and
 * sea taking the same share of the land or the sea as when half the map was sea; each is split
 * into its shades by height within its band. */
static const unsigned char thresholds[] = {
    38, 46, 54,         /* deepest, deeper, deep */
    59, 62,             /* sea, shallows */
    69,                 /* beach */
    85, 98,             /* grass, upland */
    106, 115,           /* forest, high forest */
    121, 127,           /* hills, high hills */
    134, 140,           /* mountain, high mountain */
};

/* The chance, out of 256, of a try in each row at a random cell, which becomes a town if it's
 * grassland - few of them, as most towns are by rivers... */
#define TOWN_RANDOM_CHANCE  64
/* ...and the chance, out of 256, of a try beside each river cell: towns grow up by rivers. */
#define TOWN_RIVER_CHANCE   160

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
int terrain_walk_x, terrain_walk_y, terrain_walk_to_x, terrain_walk_to_y;
unsigned* terrain_list_end;
unsigned char* terrain_list_lo;
unsigned char* terrain_list_hi;
unsigned char terrain_listing;
unsigned terrain_wander;
unsigned terrain_width, terrain_height;

static farptr cells;
static unsigned width;
static unsigned height;
/* Each row's place in the map, for cell(): its offset from the first cell, in 64-cell units - as
 * maps are a multiple of 64 cells wide - which fits in 2 bytes where an address takes 4, and
 * the C64 is short of memory. Maps are at most 256 rows high. */
static unsigned row_offset[256];

/* Grid heights: 0-127 for the coarse layer and 0-63 for the fine one, so that in 8.8 fixed
 * point every difference between two of them fits in a 16-bit int. */
static unsigned char grid_a[GRID_A_H][TERRAIN_GRID_A_W];
static unsigned char grid_b[GRID_B_H][TERRAIN_GRID_B_W];

/* The address of cell (x, y) in far memory. From a table rather than a multiply of longs, which
 * on the Plus/4 took the time of 40-odd raster lines - for every piece of a row the view reads. */
static farptr cell(unsigned x, unsigned y)
{
    return cells + ((unsigned long)row_offset[(unsigned char)y] << 6) + x;
}

unsigned char world_create(void)
{
    unsigned char i;
    unsigned h;
    unsigned char t;
    unsigned offset;

    for (i = 0; i < SIZE_COUNT; ++i)
    {
        if ((unsigned long)sizes[i][0] * sizes[i][1] <= far_maxavail())
        {
            cells = far_alloc((unsigned long)sizes[i][0] * sizes[i][1]);
            if (cells != FAR_NULL)
            {
                width = terrain_width = sizes[i][0];
                height = terrain_height = sizes[i][1];
                break;
            }
        }
    }
    if (cells == FAR_NULL)
        return 0;
    for (h = 0, offset = 0; h < height; ++h, offset += width >> 6)
        row_offset[h] = offset;

    terrain_columns_a = width / TERRAIN_SPAN_A + 1;
    terrain_columns_b = width / TERRAIN_SPAN_B + 1;

    /* A lookup table from height to tile, so each cell is one table read. Heights of 240 and
     * up only happen when the roughness pushes a height below 0 and it wraps round, so
     * they're the deepest sea too. */
    for (h = 0; h < 256; ++h)
    {
        if (h >= 240)
            t = TILE_DEEPEST;
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

/* ------------------------------------------------------------------------
 * Rivers. Before the rows are built, each river is traced downhill over the fine grid's points
 * (every TERRAIN_SPAN_B cells, where the height is just the two grids' - see point_height):
 * from a random point high enough for mountains, always to the lowest neighbouring point it
 * hasn't been to yet - even if that's higher, to get out of a hollow - until it reaches the sea or
 * joins an earlier river. A river that gets lost on the way is dropped, so every river reaches
 * the sea. Between two points a river wanders, through a midpoint pushed a little to one side, a
 * cell at a time, with its own small random number generator, seeded the same for a segment each
 * time. Its cells are then listed by row (list_river_cells: walked once to count each row's, once
 * to fill them in), so that as each row is built, its river cells are just looked up and put over
 * the land (add_rivers).
 */

#define SEA_LEVEL        (thresholds[TILE_SHALLOWS])   /* heights below this are sea */
#define SOURCE_LEVEL     (thresholds[TILE_HIGH_HILLS]) /* ...and from this up, mountains */
#define MAX_RIVERS       24
#define MAX_RIVER_POINTS 600                /* all the rivers' points together */
#define MAX_RIVER_LEN    80                 /* points in one river */
#define POINTS           (TERRAIN_GRID_B_W * GRID_B_H)
#define POINT_BYTES      ((POINTS + 7) / 8)  /* a bit for each point */
#define MAX_RIVER_CELLS  1200               /* all the rivers' cells together */

/* The rivers' working memory, in world_generate's scratch memory - the heap is far too small on
 * the C64 (about 3.5K, between the BSS and the stack) and only just big enough on the Plus/4: */
static unsigned char* river_px;             /* each point's grid column... */
static unsigned char* river_py;             /* ...and row, river after river */
static unsigned* river_start;               /* where each river's points start, then the end */
static unsigned* river_seed;                /* each river's seed for its wandering */
static unsigned char* on_river;             /* a bit for each grid point on a river */
static unsigned char* visited;              /* ...and each point the river being traced has been to */
/* Cell lists (list_cells): where each map row's cells end, and each cell's column - its low 8
 * bits in a byte, and its 9th in a bit map - which takes 1.125 bytes a cell where an int takes 2.
 * terrain_list_end, _lo and _hi (terrain.h) are the list being filled, the rivers' or the
 * roads'. */
static unsigned* river_row_end;
static unsigned char* river_cell_lo;
static unsigned char* river_cell_hi;
static unsigned char river_count;
static unsigned river_points;

/* The fine grid's size for this map, and each grid row's first point in the bit maps. */
static unsigned char points_w, points_h;
static unsigned point_row[GRID_B_H];

/* A bit within a byte, as cc65 shifts by a variable count a bit at a time. */
static const unsigned char bit_of[8] = { 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80 };

/* The eight neighbouring points. */
static const signed char next_x[8] = { -1, 0, 1, -1, 1, -1, 0, 1 };
static const signed char next_y[8] = { -1, -1, -1, 0, 0, 1, 1, 1 };

/* How far a segment's midpoint is pushed to one side, in cells, picked by 3 random bits. */
static const signed char wander[8] = { -2, -1, -1, 0, 0, 1, 1, 2 };

/* The terrain's height at each fine grid point (cx, cy) - cell (cx * 8, cy * 8) - without the
 * per-cell roughness, worked out once by point_heights: tracing the rivers looks it up thousands
 * of times. */
static unsigned char* heights;
#define point_height(cx, cy)  heights[point_row[cy] + (cx)]

/* Fills in heights: the coarse grid's height at each point, interpolated across and down as
 * terrain_row and terrain_next_row do - 4 fine points to a coarse span each way - plus the fine
 * grid point's own. The interpolation is stepped along by adding, in sixteenths. */
static unsigned col_a[TERRAIN_GRID_A_W];    /* the coarse columns' heights at a point row, x 4 */

static void point_heights(void)
{
    unsigned char cy, cx, ax, fy, last_ax = terrain_columns_a - 1;
    const unsigned char* r0;
    const unsigned char* r1;
    const unsigned char* fine;
    unsigned char* out;
    unsigned h;
    int step;

    for (cy = 0; cy < points_h; ++cy)
    {
        /* Each coarse column's height down at this row: 4 point rows to a coarse row. */
        r0 = grid_a[cy >> 2];
        fy = cy & 3;
        r1 = fy ? r0 + TERRAIN_GRID_A_W : r0;
        for (ax = 0; ax <= last_ax; ++ax)
        {
            h = r0[ax] << 2;
            step = (int)r1[ax] - r0[ax];
            for (cx = 0; cx < fy; ++cx)
                h += step;
            col_a[ax] = h;
        }

        /* Then across, and the fine grid's own height on top. */
        fine = grid_b[cy];
        out = heights + point_row[cy];
        for (ax = 0; ax < last_ax; ++ax)
        {
            h = col_a[ax] << 2;
            step = (int)col_a[ax + 1] - (int)col_a[ax];
            for (cx = 0; cx < 4; ++cx)
            {
                *out++ = (unsigned char)(h >> 4) + *fine++;
                h += step;
            }
        }
        *out = (unsigned char)(col_a[last_ax] >> 2) + *fine;
    }
}

/* A grid point's place in the bit maps: its byte, and its bit there. */
static unsigned point_byte;
static unsigned char point_bit;

static void locate_point(unsigned char cx, unsigned char cy)
{
    unsigned i = point_row[cy] + cx;
    point_byte = i >> 3;
    point_bit = bit_of[i & 7];
}

/* Traces a river down from grid point (x, y), adding its points to the river being built.
 * Returns whether it reached the sea or another river; if not, the points are taken back. */
static unsigned char trace_river(unsigned char x, unsigned char y)
{
    unsigned first = river_points;
    unsigned char len, d, nx, ny, h, best, bx, by;

    memset(visited, 0, POINT_BYTES);
    for (len = 0; len < MAX_RIVER_LEN && river_points < MAX_RIVER_POINTS; ++len)
    {
        river_px[river_points] = x;
        river_py[river_points] = y;
        ++river_points;
        locate_point(x, y);
        visited[point_byte] |= point_bit;
        if (len && (on_river[point_byte] & point_bit))
            return 1;                       /* joined another river */
        if (point_height(x, y) < SEA_LEVEL)
            return 1;                       /* reached the sea */

        /* On to the lowest neighbour not yet visited, away from the map's edge points. */
        best = 255;
        for (d = 0; d < 8; ++d)
        {
            nx = x + next_x[d];
            ny = y + next_y[d];
            if (nx == 0 || ny == 0 || nx >= points_w - 1 || ny >= points_h - 1)
                continue;
            locate_point(nx, ny);
            if (visited[point_byte] & point_bit)
                continue;
            h = point_height(nx, ny);
            if (h < best)
            {
                best = h;
                bx = nx;
                by = ny;
            }
        }
        if (best == 255)
            break;                          /* boxed in */
        x = bx;
        y = by;
    }
    river_points = first;
    return 0;
}

/* The grid point a river from (x, y) would start from: climbs to the highest neighbour until
 * there isn't a higher one, away from the map's edge points - rivers start at summits - and leaves
 * the summit in climb_x, climb_y. Returns whether it's high enough for a source, and not on or
 * next to a river already. */
static unsigned char climb_x, climb_y;

static unsigned char find_source(unsigned char x, unsigned char y)
{
    unsigned char d, nx, ny, h, best, bx, by, steps;

    for (steps = 0; steps < 64; ++steps)
    {
        best = point_height(x, y);
        bx = x;
        by = y;
        for (d = 0; d < 8; ++d)
        {
            nx = x + next_x[d];
            ny = y + next_y[d];
            if (nx == 0 || ny == 0 || nx >= points_w - 1 || ny >= points_h - 1)
                continue;
            h = point_height(nx, ny);
            if (h > best)
            {
                best = h;
                bx = nx;
                by = ny;
            }
        }
        if (bx == x && by == y)
            break;
        x = bx;
        y = by;
    }
    climb_x = x;
    climb_y = y;
    if (point_height(x, y) < SOURCE_LEVEL)
        return 0;
    for (d = 0; d < 8; ++d)
    {
        locate_point(x + next_x[d], y + next_y[d]);
        if (on_river[point_byte] & point_bit)
            return 0;
    }
    locate_point(x, y);
    return !(on_river[point_byte] & point_bit);
}

/* Traces the rivers: a try for every 48 grid points, from the summit above a random point. */
static void plan_rivers(void)
{
    unsigned tries, i;
    unsigned char x, y, r;

    river_count = 0;
    river_points = 0;
    points_w = terrain_columns_b;
    points_h = (unsigned char)(height / TERRAIN_SPAN_B + 1);
    for (y = 0; y < points_h; ++y)
        point_row[y] = (unsigned)y * points_w;
    point_heights();
    memset(on_river, 0, POINT_BYTES);

    for (tries = (unsigned)points_w * points_h / 48; tries && river_count < MAX_RIVERS; --tries)
    {
        /* A random point, not on the map's edge: the grid is a power of two cells wide and high,
         * plus one point. */
        x = 1 + (rand() & (points_w - 2));
        y = 1 + (rand() & (points_h - 2));
        if (x >= points_w - 1 || y >= points_h - 1 || !find_source(x, y))
            continue;
        r = river_count;
        river_start[r] = river_points;
        if (!trace_river(climb_x, climb_y))
            continue;
        for (i = river_start[r]; i < river_points; ++i)
        {
            locate_point(river_px[i], river_py[i]);
            on_river[point_byte] |= point_bit;
        }
        river_seed[r] = rand() | 1;
        ++river_count;
    }
    river_start[river_count] = river_points;
}

/* The wandering's random numbers: a 16-bit xorshift, so a segment can be replayed exactly. */
static unsigned char next_wander(void)
{
    terrain_wander ^= terrain_wander << 7;
    terrain_wander ^= terrain_wander >> 9;
    terrain_wander ^= terrain_wander << 8;
    return (unsigned char)terrain_wander;
}

/* Counts cell (x, y) in its row, or lists it, if it's on the map (terrain_mark, terrain.s). */
static void mark_cell(int x, int y)
{
    terrain_walk_x = x;
    terrain_walk_y = y;
    terrain_mark();
}

/* Cell i's column in a list - a macro, as it's wanted for every river and road cell as the rows
 * are built: i is evaluated more than once. */
#define list_x(lo, hi, i)  (((hi)[(i) >> 3] & bit_of[(i) & 7]) ? 0x100 | (lo)[i] : (lo)[i])

/* Marks the cells from (x, y) up to, but not including, (to_x, to_y), a step at a time across or
 * down - mostly along the longer way, now and then the shorter - so the river or road stays joined
 * up (terrain_walk, terrain.s). */
static void walk(int x, int y, int to_x, int to_y)
{
    terrain_walk_x = x;
    terrain_walk_y = y;
    terrain_walk_to_x = to_x;
    terrain_walk_to_y = to_y;
    terrain_walk();
}

/* Marks every cell of the first river_count rivers: each segment from one point to the next,
 * through its midpoint pushed to one side. */
static void walk_rivers(void)
{
    unsigned char r;
    unsigned i;
    int x0, y0, x1, y1, mx, my;
    signed char vx, vy, side;

    for (r = 0; r < river_count; ++r)
    {
        for (i = river_start[r]; i + 1 < river_start[r + 1]; ++i)
        {
            x0 = river_px[i] * TERRAIN_SPAN_B;
            y0 = river_py[i] * TERRAIN_SPAN_B;
            x1 = river_px[i + 1] * TERRAIN_SPAN_B;
            y1 = river_py[i + 1] * TERRAIN_SPAN_B;
            terrain_wander = river_seed[r] + i * 0x9E5u;
            if (!terrain_wander)
                terrain_wander = 1;
            vx = river_px[i + 1] - river_px[i];
            vy = river_py[i + 1] - river_py[i];
            side = wander[next_wander() & 7];
            mx = x0 + vx * (TERRAIN_SPAN_B / 2) - vy * side;
            my = y0 + vy * (TERRAIN_SPAN_B / 2) + vx * side;
            walk(x0, y0, mx, my);
            walk(mx, my, x1, y1);
            mark_cell(x1, y1);
        }
    }
}

/* Lists the cells walk_all marks by row: row y's are cells terrain_list_end[y] up to
 * terrain_list_end[y + 1] - 1. A first walk counts each row's cells, and turns the counts into
 * where each row's list ends; a second fills the lists in from their ends, leaving
 * terrain_list_end[y] at the start of row y's. Returns 0, without the second walk, if there are
 * more than max. */
static unsigned char list_cells(void (*walk_all)(void), unsigned max)
{
    unsigned y, total;

    memset(terrain_list_end, 0, (height + 1) * sizeof(unsigned));
    terrain_listing = 0;
    walk_all();
    total = 0;
    for (y = 0; y < height; ++y)
    {
        total += terrain_list_end[y];
        terrain_list_end[y] = total;
    }
    if (total > max)
        return 0;
    terrain_list_end[height] = total;
    terrain_listing = 1;
    walk_all();
    return 1;
}

/* Lists the rivers' cells by row (list_cells). If there are more than MAX_RIVER_CELLS, the latest
 * rivers are dropped whole - rivers only ever join earlier ones, so every river left still reaches
 * the sea. */
static void list_river_cells(void)
{
    terrain_list_end = river_row_end;
    terrain_list_lo = river_cell_lo;
    terrain_list_hi = river_cell_hi;
    while (!list_cells(walk_rivers, MAX_RIVER_CELLS))
        --river_count;
}

/* Puts row y's river cells over the land in the row just built. */
static void add_rivers(unsigned y)
{
    unsigned i, end = river_row_end[y + 1];
    unsigned char* tile;

    for (i = river_row_end[y]; i < end; ++i)
    {
        tile = terrain_row_buf + list_x(river_cell_lo, river_cell_hi, i);
        if (*tile > TILE_SHALLOWS)
            *tile = TILE_RIVER;
    }
}

/* Whether map row y has a river cell at column x. */
static unsigned char river_at(unsigned x, unsigned y)
{
    unsigned i, end = river_row_end[y + 1];

    for (i = river_row_end[y]; i < end; ++i)
        if (list_x(river_cell_lo, river_cell_hi, i) == x)
            return 1;
    return 0;
}

/* ------------------------------------------------------------------------
 * The terrain at one cell, worked out just as terrain_row and terrain_next_row do for whole rows
 * (terrain.s) - every step of their 16-bit sums, shifts and wrapping round - so that towns can be
 * placed, and roads planned between them, before the rows are built.
 */

/* Each map row's place in the roughness table (scratch memory), drawn before the rows are built. */
static unsigned char* row_rough;

/* A 16-bit value shifted right n places, keeping its sign, as terrain.s does. */
static unsigned asr16(unsigned v, unsigned char n)
{
    while (n--)
        v = (v >> 1) | (v & 0x8000);
    return v;
}

/* A grid column's height at map row y, in 8.8 fixed point: the grid row above's, plus a step per
 * row towards the one below (start_band_a/b, terrain_next_row). */
static unsigned column_a(unsigned char col, unsigned char y)
{
    const unsigned char* g = grid_a[y / TERRAIN_SPAN_A];
    unsigned step = (unsigned)((int)g[TERRAIN_GRID_A_W + col] - g[col]) << 3;

    return ((unsigned)g[col] << 8) + (y & (TERRAIN_SPAN_A - 1)) * step;
}

static unsigned column_b(unsigned char col, unsigned char y)
{
    const unsigned char* g = grid_b[y / TERRAIN_SPAN_B];
    unsigned step = (unsigned)((int)g[TERRAIN_GRID_B_W + col] - g[col]) << 5;

    return ((unsigned)g[col] << 8) + (y & (TERRAIN_SPAN_B - 1)) * step;
}

/* The tile terrain_row puts at column x of map row y. */
static unsigned char terrain_tile_at(unsigned x, unsigned char y)
{
    unsigned char span = (unsigned char)(x / TERRAIN_SPAN_B), k = x & (TERRAIN_SPAN_B - 1);
    unsigned char coarse = span >> 2;
    unsigned a0 = column_a(coarse, y);
    unsigned sa = asr16(column_a(coarse + 1, y) - a0, 5);
    unsigned b0 = column_b(span, y);
    unsigned step = asr16(column_b(span + 1, y) - b0, 3) + sa;
    unsigned h = a0 + (span & 3) * (sa << 3) + b0 + k * step;

    return terrain_tile_for_height[(unsigned char)((h >> 8)
                                                   + terrain_roughness[(unsigned char)(row_rough[y] + x)])];
}

/* No town is within this many cells of another, across or down. */
#define TOWN_SPACING  10
#define MAX_TOWNS     100               /* about 50 on the biggest map */

/* The towns, in the order of their rows (world_generate's scratch memory); while they're planned,
 * the first that could still be within TOWN_SPACING rows of the row being planned, and while the
 * rows are built, the next to put in. */
static unsigned* town_x;
static unsigned char* town_y;
static unsigned char town_count;
static unsigned char town_first;

/* Where a town by a river may go: 1 or 2 cells to either side of a river cell. */
static const signed char town_side[4] = { -2, -1, 1, 2 };

/* Plans a town at column x of row y if it'll be grassland - not under a river - and no other town
 * is within TOWN_SPACING cells. Only the towns of the last TOWN_SPACING rows need checking. The
 * cheap checks go first: working out the terrain takes the longest. */
static void try_town(unsigned x, unsigned char y)
{
    unsigned char i, tile;
    unsigned dx;

    if (x >= width || town_count == MAX_TOWNS)
        return;
    while (town_first < town_count && (unsigned char)(y - town_y[town_first]) > TOWN_SPACING)
        ++town_first;
    for (i = town_first; i < town_count; ++i)
    {
        dx = town_x[i] > x ? town_x[i] - x : x - town_x[i];
        if (dx <= TOWN_SPACING)
            return;
    }
    if (river_count && river_at(x, y))
        return;
    tile = terrain_tile_at(x, y);
    if (tile != TILE_GRASS && tile != TILE_UPLAND)
        return;
    town_x[town_count] = x;
    town_y[town_count] = y;
    ++town_count;
}

/* Plans the towns, a row at a time: now and then one beside a river cell, and in about one row in
 * four a random one - so most towns, about 60% on the biggest map, are by rivers. Beside the
 * rivers first, so they get the first claim to the space towns need. */
static void plan_towns(void)
{
    unsigned y, k, end;
    unsigned char r;

    town_count = town_first = 0;
    for (y = 0; y < height; ++y)
    {
        if (river_count)
        {
            end = river_row_end[y + 1];
            for (k = river_row_end[y]; k < end; ++k)
            {
                r = (unsigned char)rand();
                if (r < TOWN_RIVER_CHANCE)
                    try_town(list_x(river_cell_lo, river_cell_hi, k) + town_side[r & 3],
                             (unsigned char)y);
            }
        }
        if ((unsigned char)rand() < TOWN_RANDOM_CHANCE)
            try_town(rand() & (width - 1), (unsigned char)y);
    }
    town_first = 0;
}

/* Puts row y's towns in the row just built. */
static void add_towns(unsigned y)
{
    while (town_first < town_count && town_y[town_first] == y)
    {
        terrain_row_buf[town_x[town_first]] = TILE_TOWN;
        ++town_first;
    }
}

/* ------------------------------------------------------------------------
 * Roads, planned once the towns are, before the rows are built. They join the towns: the shortest
 * set of them that joins every town to every other (a minimum spanning tree, found by Prim's
 * method), which always has the road from each town to its nearest. Distances count the sea on
 * the way as SEA_DISTANCE cells for each grid point, so the roads keep to the land, crossing the
 * sea only to reach towns they can't reach otherwise. Each road is planned over the fine grid's
 * points, as the rivers are: from the point by one town, a step at a time to whichever
 * neighbouring point nearer the other town is lowest - so it keeps to the valleys - and onto the
 * sea only when every such step is. Its cells, a cell at a time between the points, are listed by
 * row as the rivers' are, and put into each row as it's built: road, or bridge over a river or
 * the sea.
 */

#define MAX_ROADS        (MAX_TOWNS - 1)
#define MAX_ROAD_CELLS   6000               /* all the roads' cells together, counting overlaps */
#define SEA_DISTANCE     64                 /* what a grid point of sea adds to a distance */

static unsigned char road_count;
static unsigned* road_start;                /* each road's points in river_px/py, then the end */
static unsigned char* road_from;            /* the towns each road joins */
static unsigned char* road_to;
static unsigned* town_best;                 /* Prim's method: each town's distance to the tree... */
static unsigned char* town_link;            /* ...and the town in it that's nearest; 255 once in */
static unsigned* road_row_end;              /* the roads' cell list */
static unsigned char* road_cell_lo;
static unsigned char* road_cell_hi;

/* How many grid steps apart two points are, diagonals counting as one. */
static unsigned char steps_apart(unsigned char ax, unsigned char ay,
                                 unsigned char bx, unsigned char by)
{
    unsigned char dx = ax > bx ? ax - bx : bx - ax;
    unsigned char dy = ay > by ? ay - by : by - ay;
    return dx > dy ? dx : dy;
}

/* The fine grid point nearest cell c, across or down. */
#define POINT_OF(c)  ((unsigned char)(((c) + TERRAIN_SPAN_B / 2) / TERRAIN_SPAN_B))

/* The distance between towns i and j: across plus down, and SEA_DISTANCE for each grid point of
 * sea on the way from one to the other (a step at a time across, down or diagonally) - or, as soon
 * as it's sure to be at least limit, just something at least that: the sea only adds, so most
 * pairs Prim's method looks at are ruled out without counting it. */
static unsigned town_distance(unsigned char i, unsigned char j, unsigned limit)
{
    unsigned dx = town_x[i] > town_x[j] ? town_x[i] - town_x[j] : town_x[j] - town_x[i];
    unsigned char dy = town_y[i] > town_y[j] ? town_y[i] - town_y[j] : town_y[j] - town_y[i];
    unsigned distance = dx + dy;
    unsigned char x, y, tx, ty;

    if (distance >= limit)
        return distance;
    x = POINT_OF(town_x[i]);
    y = POINT_OF(town_y[i]);
    tx = POINT_OF(town_x[j]);
    ty = POINT_OF(town_y[j]);
    while (x != tx || y != ty)
    {
        if (x < tx)
            ++x;
        else if (x > tx)
            --x;
        if (y < ty)
            ++y;
        else if (y > ty)
            --y;
        if (distance >= limit)
            break;
        if (point_height(x, y) < SEA_LEVEL)
            distance += SEA_DISTANCE;
    }
    return distance;
}

/* Plans the road from town a to town b, adding its points to river_px/py. Every step takes it
 * nearer, so it always arrives. Returns 0 if there's no more room for points. */
static unsigned char plan_road(unsigned char a, unsigned char b)
{
    unsigned char x = POINT_OF(town_x[a]), y = POINT_OF(town_y[a]);
    unsigned char tx = POINT_OF(town_x[b]), ty = POINT_OF(town_y[b]);
    unsigned char d, nx, ny, h, apart, best_land, best_sea, land_x, land_y, sea_x, sea_y;

    for (;;)
    {
        if (river_points == MAX_RIVER_POINTS)
            return 0;
        river_px[river_points] = x;
        river_py[river_points] = y;
        ++river_points;
        if (x == tx && y == ty)
            return 1;

        /* Of the neighbours nearer the other town: the lowest on land, else the shallowest sea. */
        apart = steps_apart(x, y, tx, ty);
        best_land = 255;
        best_sea = 0;
        sea_x = 255;
        for (d = 0; d < 8; ++d)
        {
            nx = x + next_x[d];
            ny = y + next_y[d];
            if (nx >= points_w || ny >= points_h || steps_apart(nx, ny, tx, ty) >= apart)
                continue;
            h = point_height(nx, ny);
            if (h >= SEA_LEVEL)
            {
                if (h < best_land)
                {
                    best_land = h;
                    land_x = nx;
                    land_y = ny;
                }
            }
            else if (sea_x == 255 || h > best_sea)
            {
                best_sea = h;
                sea_x = nx;
                sea_y = ny;
            }
        }
        if (best_land != 255)
        {
            x = land_x;
            y = land_y;
        }
        else
        {
            x = sea_x;
            y = sea_y;
        }
    }
}

/* Plans the roads: Prim's method, from town 0, adding each time the town nearest to the towns
 * joined so far, with the road from the one it's nearest. */
static void plan_roads(void)
{
    unsigned char i, next;
    unsigned d, best;

    road_count = 0;
    river_points = 0;                       /* the rivers' points are done with */
    town_link[0] = 255;
    for (i = 1; i < town_count; ++i)
    {
        town_best[i] = town_distance(0, i, 0xFFFF);
        town_link[i] = 0;
    }
    for (;;)
    {
        best = 0xFFFF;
        next = 255;
        for (i = 1; i < town_count; ++i)
            if (town_link[i] != 255 && town_best[i] < best)
            {
                best = town_best[i];
                next = i;
            }
        if (next == 255)
            break;
        road_start[road_count] = river_points;
        road_from[road_count] = town_link[next];
        road_to[road_count] = next;
        if (!plan_road(town_link[next], next))
        {
            river_points = road_start[road_count];
            break;
        }
        ++road_count;
        town_link[next] = 255;
        for (i = 1; i < town_count; ++i)
            if (town_link[i] != 255)
            {
                d = town_distance(next, i, town_best[i]);
                if (d < town_best[i])
                {
                    town_best[i] = d;
                    town_link[i] = next;
                }
            }
    }
    road_start[road_count] = river_points;
}

/* Marks every cell of the first road_count roads: from one town, through its points, to the
 * other, wandering a little a cell at a time. */
static void walk_roads(void)
{
    unsigned char r;
    unsigned i, end;
    int x, y, nx, ny;

    for (r = 0; r < road_count; ++r)
    {
        terrain_wander = 0x3C5Au + r * 0x9E5u;
        if (!terrain_wander)
            terrain_wander = 1;
        x = town_x[road_from[r]];
        y = town_y[road_from[r]];
        end = road_start[r + 1];
        for (i = road_start[r]; i < end; ++i)
        {
            nx = river_px[i] * TERRAIN_SPAN_B;
            ny = river_py[i] * TERRAIN_SPAN_B;
            walk(x, y, nx, ny);
            x = nx;
            y = ny;
        }
        walk(x, y, town_x[road_to[r]], town_y[road_to[r]]);
        mark_cell(town_x[road_to[r]], town_y[road_to[r]]);
    }
}

/* Puts row y's road cells in the row just built: road, or bridge over a river or the sea. Towns
 * stay towns, bridges bridges. */
static void add_roads(unsigned y)
{
    unsigned i, end = road_row_end[y + 1];
    unsigned char* tile;

    for (i = road_row_end[y]; i < end; ++i)
    {
        tile = terrain_row_buf + list_x(road_cell_lo, road_cell_hi, i);
        if (*tile == TILE_TOWN || *tile == TILE_BRIDGE)
            continue;
        *tile = (*tile == TILE_RIVER || *tile <= TILE_SHALLOWS) ? TILE_BRIDGE : TILE_ROAD;
    }
}

/* The scratch memory world_generate's rivers, towns and roads need, laid out as world_generate
 * does - WORLD_SCRATCH_SIZE (world.h) has to be at least this. The tracing's bit maps and heights,
 * and Prim's method's lists, share the space the road cells' list takes later. */
#define LIST_BYTES(cells)  ((MAX_HEIGHT + 1) * sizeof(unsigned) + (cells) + ((cells) + 7) / 8)
#define GEN_SCRATCH  (LIST_BYTES(MAX_RIVER_CELLS) + MAX_TOWNS * (sizeof(unsigned) + 1) \
                      + MAX_HEIGHT \
                      + LIST_BYTES(MAX_ROAD_CELLS) + 2 * MAX_RIVER_POINTS \
                      + (2 * MAX_RIVERS + 1) * sizeof(unsigned) \
                      + (MAX_ROADS + 1) * sizeof(unsigned) + 2 * MAX_ROADS)

void world_generate(void (*row_done)(unsigned y, const unsigned char* tiles),
                    unsigned char* scratch, unsigned scratch_size)
{
    unsigned char grid_a_h = height / TERRAIN_SPAN_A + 1;
    unsigned char grid_b_h = height / TERRAIN_SPAN_B + 1;
    unsigned char cx, cy, planned;
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

    /* Everything but the terrain is planned first, in the scratch memory - without enough of it,
     * there are no rivers, towns or roads: the rivers traced and their cells listed by row; the
     * towns, where the terrain will be grassland; the roads between them, and their cells. */
    river_count = town_count = road_count = 0;
    planned = scratch_size >= GEN_SCRATCH;
    if (planned)
    {
        river_row_end = (unsigned*)scratch;
        river_cell_lo = (unsigned char*)(river_row_end + MAX_HEIGHT + 1);
        river_cell_hi = river_cell_lo + MAX_RIVER_CELLS;
        town_x = (unsigned*)(river_cell_hi + (MAX_RIVER_CELLS + 7) / 8);
        town_y = (unsigned char*)(town_x + MAX_TOWNS);
        row_rough = town_y + MAX_TOWNS;
        road_row_end = (unsigned*)(row_rough + MAX_HEIGHT);
        road_cell_lo = (unsigned char*)(road_row_end + MAX_HEIGHT + 1);
        road_cell_hi = road_cell_lo + MAX_ROAD_CELLS;
        river_px = road_cell_hi + (MAX_ROAD_CELLS + 7) / 8;
        river_py = river_px + MAX_RIVER_POINTS;
        river_start = (unsigned*)(river_py + MAX_RIVER_POINTS);
        river_seed = river_start + MAX_RIVERS + 1;
        road_start = river_seed + MAX_RIVERS;
        road_from = (unsigned char*)(road_start + MAX_ROADS + 1);
        road_to = road_from + MAX_ROADS;
        /* In the road cells' list's space until it's filled: */
        on_river = (unsigned char*)road_row_end;
        visited = on_river + POINT_BYTES;
        heights = visited + POINT_BYTES;
        town_best = (unsigned*)(heights + POINTS);
        town_link = (unsigned char*)(town_best + MAX_TOWNS);

        for (y = 0; y < height; ++y)
            row_rough[y] = (unsigned char)rand();
        plan_rivers();
        if (river_count)
            list_river_cells();
        plan_towns();
        if (town_count > 1)
        {
            plan_roads();
            terrain_list_end = road_row_end;
            terrain_list_lo = road_cell_lo;
            terrain_list_hi = road_cell_hi;
            while (!list_cells(walk_roads, MAX_ROAD_CELLS))
                --road_count;
        }
    }

    /* One row at a time: built in terrain_row_buf, then written to far memory in one go. */
    for (y = 0; y < height; ++y)
    {
        if ((y & (TERRAIN_SPAN_A - 1)) == 0)
            start_band_a(y / TERRAIN_SPAN_A);
        if ((y & (TERRAIN_SPAN_B - 1)) == 0)
            start_band_b(y / TERRAIN_SPAN_B);

        /* Each row starts at a different place in the roughness table, so rows don't repeat it. */
        terrain_roughness_start = planned ? row_rough[y] : (unsigned char)rand();
        terrain_row();
        if (river_count)
            add_rivers(y);
        if (town_count)
            add_towns(y);
        if (road_count)
            add_roads(y);
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
