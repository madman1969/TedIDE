#ifndef VIEW_H
#define VIEW_H

/* What's on screen: the map view (the top 23 rows, with a cursor in the middle) and its
 * two-line status bar, or an overview of the whole world shrunk to 40x16 characters - drawn
 * row by row while the world is generated, and on request with M. */

#define VIEW_WIDTH   40
#define VIEW_HEIGHT  23
#define CURSOR_X     (VIEW_WIDTH / 2)
#define CURSOR_Y     (VIEW_HEIGHT / 2)

/* Black screen, no cursor. */
void view_init(void);

/* Draws the part of the map whose top-left cell is (left, top), straight into screen and
 * colour RAM, with the cursor over the middle cell. */
void view_draw(unsigned left, unsigned top);

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
