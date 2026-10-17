#ifndef COLOURS_H
#define COLOURS_H

/* Draws all 16 of the VDC's RGBI colours as solid bars across the full 80
 * columns, by writing colour values straight into the VDC's attribute RAM
 * with its block-fill hardware. Returns DEMO_QUIT if Q was pressed. */
unsigned char colours_demo(void);

#endif
