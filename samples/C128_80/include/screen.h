#ifndef SCREEN_H
#define SCREEN_H

/* Switches to the C128's 80-column mode (the VDC chip), clears the screen
 * and hides the cursor. Called once at startup - every stage below runs in
 * 80-column mode unless it explicitly switches away (see contrast.c/.h). */
void screen_init(void);

/* Prints the demo's title banner at the top of the screen. Called at the
 * start of every stage, since each stage clears the screen first. */
void screen_banner(void);

/* Restores 40-column mode and the cursor on the way out. */
void screen_shutdown(void);

#endif
