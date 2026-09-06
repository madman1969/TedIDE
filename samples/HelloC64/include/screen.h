#ifndef SCREEN_H
#define SCREEN_H

/* Clears the screen, hides the cursor and prints the banner/instructions. */
void screen_init(void);

/* Restores the cursor before the program exits. */
void screen_shutdown(void);

/* Draws a single character at (x, y) in the given color. */
void screen_put_char(unsigned char x, unsigned char y, char c, unsigned char color);

#endif
