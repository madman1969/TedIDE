#ifndef SCREEN_H
#define SCREEN_H

#define SCREEN_COLS  80
#define SCREEN_ROWS  25

/* Switches to the C128's 80-column mode (the VDC chip), clears the
 * screen and turns the hardware cursor on - the editor relies on it to
 * show the caret instead of drawing its own. */
void screen_init(void);

/* Restores 40-column mode on the way out. */
void screen_shutdown(void);

#endif
