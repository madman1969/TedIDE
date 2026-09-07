#ifndef PALETTE_H
#define PALETTE_H

/* Cycles the border and background through the TED chip's full 121-color
 * palette (16 hues x 8 luminance steps, minus 7 duplicate blacks) - a range
 * the C64's VIC-II can't reach, since it only has 16 fixed colors and no
 * independent luminance control per color.
 * Returns 1 if Q was pressed while it was cycling (quit requested). */
unsigned char palette_demo(void);

#endif
