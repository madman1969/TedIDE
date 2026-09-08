#ifndef EDITOR_H
#define EDITOR_H

/* Prompts for a filename, loads it (or starts a new buffer under that
 * name if it doesn't exist yet), then runs the edit loop until the
 * user exits. */
void editor_run(void);

#endif
