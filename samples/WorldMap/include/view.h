#ifndef VIEW_H
#define VIEW_H

/* What's on screen: the map view (the top 23 rows, with a cursor in the middle) and its
 * two-line status bar, or an overview of the whole world shrunk to 40x16 characters - drawn
 * row by row while the world is generated, and on request with M. */

#define VIEW_WIDTH   40
#define VIEW_HEIGHT  23
#define CURSOR_X     (VIEW_WIDTH / 2)
#define CURSOR_Y     (VIEW_HEIGHT / 2)

/* Black screen, no cursor, and the map tiles' own character set (see charset.h). Call after
 * world_create(). */
void view_init(void);

/* Puts the screen back as BASIC had it, on the way out. */
void view_done(void);

/* Forgets the cached map tiles and the status bar, after the map has changed or the screen
 * has been cleared, so the next view_draw() reads and draws everything again. */
void view_invalidate(void);

/* Draws the part of the map whose top-left cell is (left, top), with the cursor over the
 * middle cell, and returns the tile under the cursor. Tiles come from a cache of the map
 * around the view, which reads from far memory only the rows and columns that scroll into it. */
unsigned char view_draw(unsigned left, unsigned top);

/* Shows the far memory backend, the cursor position, the tile under it, and the keys. */
void view_status(unsigned x, unsigned y, unsigned char tile);

/* Clears the screen for an overview, with a title and a line about the map and far memory. */
void view_overview_begin(const char* title);

/* Given every map row in turn (or just every sixteenth of the map's height), draws a sample of
 * the ones the overview shows. */
void view_overview_row(unsigned y, const unsigned char* tiles);

/* Marks map cell (x, y) on the overview. */
void view_overview_mark(unsigned x, unsigned y);

/* The number of map rows between two overview rows. */
unsigned view_overview_step(void);

/* Prints a percentage under the overview, while it's being generated. */
void view_progress(unsigned char percent);

#endif
