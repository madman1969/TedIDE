#ifndef VIEW_H
#define VIEW_H

/* What's on screen: the map view (with a cursor in the middle) and its two-line status bar, or
 * an overview of the whole world shrunk to 40x16 characters - drawn row by row while the world
 * is generated, and on request with M. Where each goes on the screen is up to the machine - see
 * screen.h. */

#include "screen.h"

#define VIEW_WIDTH   40
#define VIEW_HEIGHT  SCREEN_MAP_HEIGHT
#define CURSOR_X     (VIEW_WIDTH / 2)
#define CURSOR_Y     (VIEW_HEIGHT / 2)

/* View positions count fractions of a cell - eighths on the Plus/4, which scrolls a pixel at a
 * time, and whole cells on the C64. These convert. */
#define VIEW_FINE_BITS     SCREEN_FINE_BITS
#define VIEW_CELL(pos)     ((pos) >> VIEW_FINE_BITS)
#define VIEW_POS(cell)     ((unsigned)(cell) << VIEW_FINE_BITS)

/* Black screen, no cursor, and the map tiles' own character set (see screen.h). Call after
 * world_create(). */
void view_init(void);

/* Puts the screen back as BASIC had it, on the way out. */
void view_done(void);

/* Forgets the cached map tiles and the status bar, after the map has changed or the screen
 * has been cleared, so the next view_draw() reads and draws everything again. */
void view_invalidate(void);

/* Shows the map with its top-left corner at (left, top), a view position, with the cursor over
 * the middle cell and the status bar naming the cell and its tile. Tiles come from a cache of
 * the map around the view, which reads from far memory only the rows and columns that scroll
 * into it, and a move within the same cell only scrolls the screen. */
void view_draw(unsigned left, unsigned top);

/* The map cell under the cursor, as last drawn. */
unsigned view_cursor_x(void);
unsigned view_cursor_y(void);

/* Clears the screen for an overview, with a title and a line about the map and far memory. */
void view_overview_begin(const char* title);

/* Given every map row in turn (or just every sixteenth of the map's height), draws a sample of
 * the ones the overview shows. */
void view_overview_row(unsigned y, const unsigned char* tiles);

/* Marks map cell (x, y) on the overview. */
void view_overview_mark(unsigned x, unsigned y);

/* The number of map rows between two overview rows. */
unsigned view_overview_step(void);

/* Prints a percentage under the overview, while it's being generated, and flashes the border -
 * all that shows while view_generating has the display off. */
void view_progress(unsigned char percent);

/* Turns the display off while a world is generated, which makes it about twice as fast on the
 * Plus/4, or back on, with the border black again, showing the finished overview. */
void view_generating(unsigned char on);

#endif
