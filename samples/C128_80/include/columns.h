#ifndef COLUMNS_H
#define COLUMNS_H

/* Lays out two related lists side by side at column 0 and column 40 - text
 * that would collide/wrap into each other in 40-column mode, so genuinely
 * needs the 80 columns the C128's VDC provides. Returns 1 if Q was pressed
 * afterwards (quit requested). */
unsigned char columns_demo(void);

#endif
