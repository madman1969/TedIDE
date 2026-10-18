#ifndef WORLD_H
#define WORLD_H

/* The world map: one byte per cell, holding a TILE_ value, kept in far memory (see farmem.h) -
 * a 512x256 map is 128K, twice the C64's whole address space. */

/* The tiles, from the deepest sea to the highest snow, then towns, rivers, roads, bridges and
 * marsh - beach with no sea next to it. Each kind of land
 * and sea comes in shades of height, so a map can be shaded like a relief map; a machine with
 * fewer colours just gives some shades the same one. */
#define TILE_DEEPEST        0
#define TILE_DEEPER         1
#define TILE_DEEP           2
#define TILE_SEA            3
#define TILE_SHALLOWS       4
#define TILE_BEACH          5
#define TILE_GRASS          6
#define TILE_UPLAND         7
#define TILE_FOREST         8
#define TILE_HIGH_FOREST    9
#define TILE_HILLS          10
#define TILE_HIGH_HILLS     11
#define TILE_MOUNTAIN       12
#define TILE_HIGH_MOUNTAIN  13
#define TILE_SNOW           14
#define TILE_TOWN           15
#define TILE_RIVER          16
#define TILE_ROAD           17
#define TILE_BRIDGE         18              /* a road over water */
#define TILE_MARSH          19
#define TILE_COUNT          20

/* Allocates the biggest map far memory has room for, from 512x256 down to 64x32.
 * Returns 0 if not even the smallest fits. Call far_init() first. */
unsigned char world_create(void);

/* The map's size in cells. */
unsigned world_width(void);
unsigned world_height(void);

/* Fills the map with new random terrain, with rivers running from the mountains to the sea,
 * towns on the grassland - mostly by rivers, never within 10 cells of each other - and roads
 * joining every town to every other, with bridges over rivers and the sea; beach with no sea next
 * to it is marsh. scratch_size bytes at scratch are working memory for all but the terrain
 * (WORLD_SCRATCH_SIZE; with less,
 * there are none; it must be at least TERRAIN_MAX_WIDTH, 512). Calls row_done() with each row's
 * tiles as it's finished - the biggest map takes half a minute or so.
 * Seed the random number generator (srand or _randomize) first for a different world each
 * time. */
void world_generate(void (*row_done)(unsigned y, const unsigned char* tiles),
                    unsigned char* scratch, unsigned scratch_size);

/* The working memory world_generate needs for rivers, towns, roads and marsh. */
#define WORLD_SCRATCH_SIZE  11679u

/* Copies len cells of row y, starting at column x, into dst - one far_read. */
void world_read(unsigned x, unsigned y, unsigned char* dst, unsigned len);

/* Single cells, through FarMem's window cache. */
unsigned char world_get(unsigned x, unsigned y);
void world_set(unsigned x, unsigned y, unsigned char tile);

#endif
