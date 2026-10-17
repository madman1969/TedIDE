#ifndef CHARSET_H
#define CHARSET_H

/* Redefines four characters in the VDC's character RAM and fills the screen
 * with them, then animates an invader by redrawing one glyph - every copy on
 * screen changes at once. Restores the ROM character set before returning.
 * Returns DEMO_QUIT or DEMO_NEXT, having waited for its own key. */
unsigned char charset_demo(void);

#endif
