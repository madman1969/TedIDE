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
/* Each row's address in far memory, for cell(): maps are at most 256 rows high. */
static farptr row_address[256];

/* Grid heights: 0-127 for the coarse layer and 0-63 for the fine one, so that in 8.8 fixed
 * point every difference between two of them fits in a 16-bit int. */
static unsigned char grid_a[GRID_A_H][TERRAIN_GRID_A_W];
static unsigned char grid_b[GRID_B_H][TERRAIN_GRID_B_W];

/* The address of cell (x, y) in far memory. From a table rather than a multiply of longs, which
 * on the Plus/4 took the time of 40-odd raster lines - for every piece of a row the view reads. */
static farptr cell(unsigned x, unsigned y)
{
    return row_address[(unsigned char)y] + x;
}

unsigned char world_create(void)
{
    unsigned char i;
    unsigned h;
    unsigned char t;
    farptr row;

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
    row = cells;
    for (h = 0; h < height; ++h)
    {
        row_address[h] = row;
        row += width;
    }

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
 * the sea. Then, as the rows are built, each band of TERRAIN_SPAN_B rows gets the river cells it
 * holds marked in a bit mask (river_band), and they're put over the land in each row
 * (add_rivers): between two points a river wanders, through a midpoint pushed a little to one
 * side, a cell at a time - its own small random number generator, seeded the same for each
 * segment every time, makes a segment that crosses two bands come out the same in both.
 */

#define SEA_LEVEL        (thresholds[TILE_SHALLOWS])   /* heights below this are sea */
#define SOURCE_LEVEL     (thresholds[TILE_HIGH_HILLS]) /* ...and from this up, mountains */
#define MAX_RIVERS       24
#define MAX_RIVER_POINTS 600                /* all the rivers' points together */
#define MAX_RIVER_LEN    80                 /* points in one river */
#define POINT_BYTES      ((TERRAIN_GRID_B_W * GRID_B_H + 7) / 8)  /* a bit for each point */
#define MASK_STRIDE      (TERRAIN_MAX_WIDTH / 8)
#define BAND_MASK_BYTES  (TERRAIN_SPAN_B * MASK_STRIDE)

/* The rivers' working memory, allocated for world_generate and freed after: */
static unsigned char* river_px;             /* each point's grid column... */
static unsigned char* river_py;             /* ...and row, river after river */
static unsigned* river_start;               /* where each river's points start, then the end */
static unsigned* river_seed;                /* each river's seed for its wandering */
static unsigned char* on_river;             /* a bit for each grid point on a river */
static unsigned char* visited;              /* ...and each point the river being traced has been to */
static unsigned char* band_mask;            /* a bit for each river cell in the band being built */
static unsigned char river_count;
static unsigned river_points;

/* The fine grid's size for this map, and each grid row's first point in the bit maps. */
static unsigned char points_w, points_h;
static unsigned point_row[GRID_B_H];

/* A bit within a byte, as cc65 shifts by a variable count a bit at a time. */
static const unsigned char bit_of[8] = { 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80 };
static const unsigned char cell_bit[8] = { 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 };

/* The eight neighbouring points. */
static const signed char next_x[8] = { -1, 0, 1, -1, 1, -1, 0, 1 };
static const signed char next_y[8] = { -1, -1, -1, 0, 0, 1, 1, 1 };

/* How far a segment's midpoint is pushed to one side, in cells, picked by 3 random bits. */
static const signed char wander[8] = { -2, -1, -1, 0, 0, 1, 1, 2 };

/* The terrain's height at fine grid point (cx, cy) - cell (cx * 8, cy * 8) - without the per-cell
 * roughness: the coarse grid's height there, interpolated across and down as terrain_row and
 * terrain_next_row do, plus the fine grid point's own. Never at the map's edge points, so the
 * coarse grid point to the right and below always exist. */
static unsigned char point_height(unsigned char cx, unsigned char cy)
{
    unsigned char ax = cx >> 2, fx = cx & 3, fy = cy & 3;
    const unsigned char* r0 = grid_a[cy >> 2];
    const unsigned char* r1 = r0 + TERRAIN_GRID_A_W;
    unsigned top = r0[ax] * (4 - fx) + r0[ax + 1] * fx;
    unsigned bottom = r1[ax] * (4 - fx) + r1[ax + 1] * fx;
    return (unsigned char)((top * (4 - fy) + bottom * fy) >> 4) + grid_b[cy][cx];
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
static unsigned wander_state;

static unsigned char next_wander(void)
{
    wander_state ^= wander_state << 7;
    wander_state ^= wander_state >> 9;
    wander_state ^= wander_state << 8;
    return (unsigned char)wander_state;
}

/* The band river_band is marking: its first row. */
static int band_top;

/* Marks cell (x, y) in band_mask, if it's in the band and on the map. */
static void mark_cell(int x, int y)
{
    y -= band_top;
    if (y < 0 || y >= TERRAIN_SPAN_B || x < 0 || x >= (int)width)
        return;
    band_mask[((unsigned char)y << 6) + ((unsigned)x >> 3)] |= cell_bit[x & 7];
}

/* Marks the cells from (x, y) up to, but not including, (to_x, to_y), a step at a time across or
 * down - mostly along the longer way, now and then the shorter - so the river stays joined up. */
static void walk(int x, int y, int to_x, int to_y)
{
    int dx, dy;
    unsigned adx, ady;
    unsigned char across;

    while (x != to_x || y != to_y)
    {
        mark_cell(x, y);
        dx = to_x - x;
        dy = to_y - y;
        adx = dx < 0 ? -dx : dx;
        ady = dy < 0 ? -dy : dy;
        if (ady == 0)
            across = 1;
        else if (adx == 0)
            across = 0;
        else
            across = (adx >= ady) ^ ((next_wander() & 3) == 0);
        if (across)
            x += dx < 0 ? -1 : 1;
        else
            y += dy < 0 ? -1 : 1;
    }
}

/* Marks the river cells in rows y to y + TERRAIN_SPAN_B - 1 in band_mask. */
static void river_band(unsigned y)
{
    unsigned char r;
    unsigned i;
    int x0, y0, x1, y1, mx, my, top, bottom;
    signed char vx, vy, side;

    memset(band_mask, 0, BAND_MASK_BYTES);
    band_top = (int)y;
    for (r = 0; r < river_count; ++r)
    {
        for (i = river_start[r]; i + 1 < river_start[r + 1]; ++i)
        {
            x0 = river_px[i] * TERRAIN_SPAN_B;
            y0 = river_py[i] * TERRAIN_SPAN_B;
            x1 = river_px[i + 1] * TERRAIN_SPAN_B;
            y1 = river_py[i + 1] * TERRAIN_SPAN_B;
            /* Only segments that can reach the band, with the midpoint's push. */
            top = (y0 < y1 ? y0 : y1) - 2;
            bottom = (y0 < y1 ? y1 : y0) + 2;
            if (bottom < band_top || top >= band_top + TERRAIN_SPAN_B)
                continue;
            wander_state = river_seed[r] + i * 0x9E5u;
            if (!wander_state)
                wander_state = 1;
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

/* Puts the river cells marked for row r of the band over the land in the row just built. */
static void add_rivers(unsigned char r)
{
    const unsigned char* mask = band_mask + ((unsigned)r << 6);
    unsigned char i, b, bits;
    unsigned x;

    for (i = 0; i < (unsigned char)(width >> 3); ++i)
    {
        bits = mask[i];
        if (!bits)
            continue;
        x = (unsigned)i << 3;
        for (b = 0; b < 8; ++b, ++x)
            if ((bits & cell_bit[b]) && terrain_row_buf[x] > TILE_SHALLOWS)
                terrain_row_buf[x] = TILE_RIVER;
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
        if (terrain_row_buf[x] == TILE_GRASS || terrain_row_buf[x] == TILE_UPLAND)
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

    /* The rivers, in memory that's only needed until the map is built - with none if there's
     * no room for it. */
    river_count = 0;
    river_px = malloc(2 * MAX_RIVER_POINTS + (2 * MAX_RIVERS + 1) * sizeof(unsigned)
                      + 2 * POINT_BYTES + BAND_MASK_BYTES);
    if (river_px)
    {
        river_py = river_px + MAX_RIVER_POINTS;
        river_start = (unsigned*)(river_py + MAX_RIVER_POINTS);
        river_seed = river_start + MAX_RIVERS + 1;
        on_river = (unsigned char*)(river_seed + MAX_RIVERS);
        visited = on_river + POINT_BYTES;
        band_mask = visited + POINT_BYTES;
        plan_rivers();
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
        if (river_count && (y & (TERRAIN_SPAN_B - 1)) == 0)
            river_band(y);
        terrain_row();
        if (river_count)
            add_rivers((unsigned char)(y & (TERRAIN_SPAN_B - 1)));
        place_towns();
        far_write(row, terrain_row_buf, width);
        row += width;
        terrain_next_row();

        row_done(y, terrain_row_buf);
    }
    free(river_px);                         /* free(0) does nothing */
    river_px = 0;
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
