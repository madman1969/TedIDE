#ifndef FILEIO_H
#define FILEIO_H

/* Replaces the buffer's contents with the lines read from `filename`.
 * Returns 0 if the file can't be opened (caller should fall back to
 * starting a new, empty buffer under that name). */
unsigned char fileio_load(const char *filename);

/* Writes the buffer's contents to `filename`, one line per record.
 * Returns 0 if the file can't be written. */
unsigned char fileio_save(const char *filename);

#endif
