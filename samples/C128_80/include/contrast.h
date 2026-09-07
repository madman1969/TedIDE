#ifndef CONTRAST_H
#define CONTRAST_H

/* Switches live to 40-column mode and draws the same ruler ruler_draw()
 * uses, so it visibly wraps after column 39 instead of reaching 79 - then
 * switches back to 80-column mode before returning, proving videomode()
 * genuinely changes the screen width rather than just being a cosmetic
 * setting. Returns 1 if Q was pressed afterwards (quit requested). */
unsigned char contrast_demo(void);

#endif
