#ifndef INPUT_H
#define INPUT_H

/* What a demo returns to main.c's run_stage(). */
#define DEMO_CONTINUE 0   /* show the "next demo" prompt and wait for a key */
#define DEMO_QUIT     1   /* Q was pressed */
#define DEMO_NEXT     2   /* the demo already waited for its own key */

/* Non-blocking: returns 1 if Q/q has been pressed since the last poll. */
unsigned char input_quit_requested(void);

/* Blocks until a key is pressed. Returns 1 if it was Q/q (quit requested),
 * 0 for any other key (continue). */
unsigned char input_wait_continue(void);

/* Blocks until a key is pressed, for demos that animate until one is.
 * Returns DEMO_QUIT for Q/q, DEMO_NEXT for anything else. */
unsigned char input_wait_next(void);

#endif
