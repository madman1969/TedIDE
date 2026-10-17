#ifndef WORLD_H
#define WORLD_H

/* The world map: one byte per cell, holding a TILE_ value, kept in far memory (see farmem.h) -
 * a 512x256 map is 128K, twice the C64's whole address space. */

#define TILE_DEEP      0
#define TILE_SEA       1
#define TILE_BEACH     2
#define TILE_GRASS     3
#define TILE_FOREST    4
#define TILE_HILLS     5
#define TILE_MOUNTAIN  6
#define TILE_SNOW      7
#define TILE_TOWN      8
#define TILE_COUNT     9

/* Allocates the biggest map far memory has room for, from 512x256 down to 64x32.
 * Returns 0 if not even the smallest fits. Call far_init() first. */
unsigned char world_create(void);

/* The map's size in cells. */
unsigned world_width(void);
unsigned world_height(void);

/* Fills the map with new random terrain, with towns scattered over the grassland, calling
 * row_done() with each row's tiles as it's finished - the biggest map takes several seconds.
 * Seed the random number generator (srand or _randomize) first for a different world each
 * time. */
void world_generate(void (*row_done)(unsigned y, const unsigned char* tiles));

/* Copies len cells of row y, starting at column x, into dst - one far_read. */
void world_read(unsigned x, unsigned y, unsigned char* dst, unsigned len);

/* Single cells, through FarMem's window cache. */
unsigned char world_get(unsigned x, unsigned y);
void world_set(unsigned x, unsigned y, unsigned char tile);

#endif
