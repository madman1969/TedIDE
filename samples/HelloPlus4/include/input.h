#ifndef INPUT_H
#define INPUT_H

/* Non-blocking: returns 1 if Q/q has been pressed since the last poll. */
unsigned char input_quit_requested(void);

/* Blocks until a key is pressed. Returns 1 if it was Q/q (quit requested),
 * 0 for any other key (continue). */
unsigned char input_wait_continue(void);

#endif
