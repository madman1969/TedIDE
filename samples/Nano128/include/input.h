#ifndef INPUT_H
#define INPUT_H

#define CONFIRM_NO      0
#define CONFIRM_YES     1
#define CONFIRM_CANCEL  2

/* Reads a line of text at screen row `row`, starting at column `col`
 * (the caller has already drawn a prompt/label before that column).
 * Supports Backspace and prints as the user types. Enter confirms;
 * Esc or Run/Stop cancels and leaves `buf` untouched.
 * `buf` must have room for maxlen+1 bytes. Returns 1 if confirmed,
 * 0 if cancelled. */
unsigned char input_line(unsigned char row, unsigned char col, char *buf, unsigned char maxlen);

/* Waits for Y, N, Esc or Run/Stop and returns one of the CONFIRM_*
 * values above (Esc/Stop both cancel). */
unsigned char input_confirm(void);

#endif
