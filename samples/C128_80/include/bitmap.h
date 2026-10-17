#ifndef BITMAP_H
#define BITMAP_H

/* Switches the VDC into its 640x200 bitmap mode with cc65's c128-vdc TGI
 * driver, draws lines, a bar chart, circles and an ellipse, then returns to
 * text mode. Returns DEMO_QUIT or DEMO_NEXT, having waited for its own key. */
unsigned char bitmap_demo(void);

#endif
