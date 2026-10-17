#ifndef EFFECTS_H
#define EFFECTS_H

/* Moves the whole display in a small circle a pixel at a time with the VDC's
 * horizontal and vertical smooth-scroll registers, once per vertical blank,
 * and flips the entire screen to reverse video every few seconds. Restores
 * the registers before returning. Returns DEMO_QUIT or DEMO_NEXT, having
 * waited for its own key. */
unsigned char effects_demo(void);

#endif
