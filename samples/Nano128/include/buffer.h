#ifndef BUFFER_H
#define BUFFER_H

/* A document is a list of malloc'd, NUL-terminated C strings, one per
 * line, each grown/shrunk with realloc() as it's edited - so memory use
 * tracks what's actually typed rather than a worst-case grid. The caps
 * below just bound the array of line pointers and how long a single
 * line may get; they're generous for an 80-column screen, not a promise
 * that much RAM is free. */
#define BUF_MAX_LINES     500
#define BUF_MAX_LINE_LEN  250

void buffer_init(void);
void buffer_free_all(void);

unsigned int buffer_line_count(void);
unsigned char buffer_line_len(unsigned int line);
const char *buffer_line_text(unsigned int line);

unsigned char buffer_modified(void);
void buffer_set_modified(unsigned char flag);

/* Inserts a copy of `text` as a new line at index `at`, shifting later
 * lines down. Returns 0 if the buffer is full or out of memory. */
unsigned char buffer_insert_line(unsigned int at, const char *text);

/* Removes the line at index `at`, shifting later lines up. */
void buffer_delete_line(unsigned int at);

/* Splits `line` at column `col`: the text before `col` stays on `line`,
 * the text from `col` onward becomes a new line right after it. */
unsigned char buffer_split_line(unsigned int line, unsigned char col);

/* Appends the text of the following line onto the end of `line`, then
 * removes that following line. Fails (returns 0) if the combined line
 * would exceed BUF_MAX_LINE_LEN. */
unsigned char buffer_join_next_line(unsigned int line);

/* Inserts character `c` into `line` just before column `col`. */
unsigned char buffer_insert_char(unsigned int line, unsigned char col, unsigned char c);

/* Deletes the character at column `col` of `line` (the char the cursor
 * is on), shifting the rest of the line left. */
unsigned char buffer_delete_char(unsigned int line, unsigned char col);

#endif
