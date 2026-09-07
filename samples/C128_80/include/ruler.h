#ifndef RULER_H
#define RULER_H

/* Draws a column-number ruler ('0'-'9' repeating) across every column of
 * the given row, using the current screen width from screensize() - so it
 * automatically spans 40 or 80 columns depending on the current video
 * mode. Shared with contrast.c's side-by-side comparison. */
void ruler_draw(unsigned char row);

/* Reports the current screen size and draws a ruler across the whole row,
 * proving 80-column mode is genuinely active rather than just requested -
 * see contrast.c for a side-by-side comparison against 40-column mode.
 * Returns 1 if Q was pressed afterwards (quit requested). */
unsigned char ruler_demo(void);

#endif
