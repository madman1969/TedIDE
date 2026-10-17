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

/* Where terrain_row() puts the row it builds. */
extern unsigned char terrain_row_buf[TERRAIN_MAX_WIDTH];

/* Fills terrain_row_buf with the current row's tiles: terrain_columns_b - 1 spans of
 * TERRAIN_SPAN_B cells each. */
void terrain_row(void);

/* Moves every grid column's height one row further down. */
void terrain_next_row(void);

#endif
