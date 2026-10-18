#ifndef TERRAIN_H
#define TERRAIN_H

/* The two loops world.c runs for every row of the map, written in assembly (terrain.s) - they
 * run for up to 131,072 cells, and in C the map took over half a minute to generate. world.c
 * fills in the tables below; terrain.s only reads them, except where noted. */

#define TERRAIN_MAX_WIDTH  512
#define TERRAIN_SPAN_A     32
#define TERRAIN_SPAN_B     8
#define TERRAIN_GRID_A_W   (TERRAIN_MAX_WIDTH / TERRAIN_SPAN_A + 1)
#define TERRAIN_GRID_B_W   (TERRAIN_MAX_WIDTH / TERRAIN_SPAN_B + 1)

/* Each grid column's height at the current row, in 8.8 fixed point split into low and high
 * bytes (the 6502 indexes byte arrays far more cheaply than word arrays), and its change per
 * row. terrain_next_row() updates the heights. */
extern unsigned char terrain_col_a_lo[TERRAIN_GRID_A_W];
extern unsigned char terrain_col_a_hi[TERRAIN_GRID_A_W];
extern unsigned char terrain_step_a_lo[TERRAIN_GRID_A_W];
extern unsigned char terrain_step_a_hi[TERRAIN_GRID_A_W];
extern unsigned char terrain_col_b_lo[TERRAIN_GRID_B_W];
extern unsigned char terrain_col_b_hi[TERRAIN_GRID_B_W];
extern unsigned char terrain_step_b_lo[TERRAIN_GRID_B_W];
extern unsigned char terrain_step_b_hi[TERRAIN_GRID_B_W];

/* How many columns each grid has for the current map size. */
extern unsigned char terrain_columns_a;
extern unsigned char terrain_columns_b;

/* Per-cell roughness (-4 to +3, as unsigned bytes), and where in it the next row starts. */
extern unsigned char terrain_roughness[256];
extern unsigned char terrain_roughness_start;

/* The tile for each total height. */
extern unsigned char terrain_tile_for_height[256];

/* Where terrain_row() puts the row it builds: world.c builds rows into three buffers in turn. */
extern unsigned char* terrain_row_out;

/* Fills terrain_row_out with the current row's tiles: terrain_columns_b - 1 spans of
 * TERRAIN_SPAN_B cells each. */
void terrain_row(void);

/* Moves every grid column's height one row further down. */
void terrain_next_row(void);

/* Rivers and roads are drawn by walking a cell at a time, and their cells listed by row (world.c's
 * list_cells), which for the longest roads is thousands of cells, twice over: in assembly too.
 *
 * terrain_walk() marks the cells from (terrain_walk_x, terrain_walk_y) up to, but not including,
 * (terrain_walk_to_x, terrain_walk_to_y), a step at a time across or down - mostly along the longer
 * way, now and then the shorter, as terrain_wander's random numbers say - leaving terrain_walk_x
 * and _y at the end. terrain_mark() marks just (terrain_walk_x, terrain_walk_y).
 *
 * Marking a cell on the map (terrain_width by terrain_height) either counts it in its row -
 * ++terrain_list_end[y] - or, if terrain_listing, lists it: i = --terrain_list_end[y], with the
 * column's low 8 bits in terrain_list_lo[i] and its 9th in bit i of terrain_list_hi. */
extern int terrain_walk_x, terrain_walk_y, terrain_walk_to_x, terrain_walk_to_y;
extern unsigned* terrain_list_end;
extern unsigned char* terrain_list_lo;
extern unsigned char* terrain_list_hi;
extern unsigned char terrain_listing;
extern unsigned terrain_wander;             /* a 16-bit xorshift's state: never 0 */
extern unsigned terrain_width, terrain_height;

void terrain_walk(void);
void terrain_mark(void);

/* Turns the beach cells of the row at terrain_marsh_row with no sea in the 8 cells around them -
 * sea, shallows or a bridge, in that row or the rows at terrain_marsh_above and _below (0 for none)
 * - into marsh. */
extern unsigned char* terrain_marsh_row;
extern unsigned char* terrain_marsh_above;
extern unsigned char* terrain_marsh_below;

void terrain_marsh(void);

/* Putting a row's road cells in (world.c's add_roads). terrain_road_bits(0) maps the listed road
 * cells terrain_road_from to terrain_road_to - 1 (in terrain_road_lo and _hi, as the other cell
 * lists) for the row being finished; terrain_road_bits(1) maps the row below's into
 * terrain_road_below, where world.c adds that row's towns. terrain_add_roads() then puts them in
 * the row at terrain_road_tiles - road, or bridge over a river or the sea - leaving out any that
 * would only make a road two cells wide with the finished row above (terrain_road_above, 0 for
 * none): one that would complete a 2x2 square of road, with nothing else joining it. */
extern unsigned char* terrain_road_lo;
extern unsigned char* terrain_road_hi;
extern unsigned terrain_road_from, terrain_road_to;
extern unsigned char* terrain_road_tiles;
extern unsigned char* terrain_road_above;
extern unsigned char terrain_road_below[TERRAIN_MAX_WIDTH / 8];

void __fastcall__ terrain_road_bits(unsigned char below);
void terrain_add_roads(void);

/* The cheapest way from point terrain_route_start of a window of grid points to every other
 * (world.c's plan_road; terrain.s explains what to set up first): terrain_route_w points across
 * (at most 126), terrain_route_points in all, numbered across then down; a step onto a point
 * costs its terrain_route_cost, $FF for a wall, and every edge point must be one. Leaves each
 * point's cheapest cost in terrain_route_dist_lo/_hi, and the direction of the step that got
 * there in the low 3 bits of its terrain_route_parent; that step's offset from w + 1 points
 * before the point is in terrain_route_off. */
extern unsigned char terrain_route_w;
extern unsigned terrain_route_points;
extern unsigned terrain_route_start;
extern unsigned char terrain_route_off[8];
extern unsigned char* terrain_route_cost;
extern unsigned char* terrain_route_parent;
extern unsigned char* terrain_route_dist_lo;
extern unsigned char* terrain_route_dist_hi;
extern unsigned char* terrain_route_queue;

void terrain_route(void);

#endif
