#ifndef SCREEN_H
#define SCREEN_H

/* Clears the screen and hides the cursor. Called once at startup. */
void screen_init(void);

/* Prints the demo's title banner at the top of the screen. Called at the
 * start of every stage, since each stage clears the screen first. */
void screen_banner(void);

/* Restores the cursor and clears the screen on the way out. */
void screen_shutdown(void);

#endif
