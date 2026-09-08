/*
 * Tedide sample: Nano128, a small screen editor for the Commodore 128
 * that replicates GNU nano's basic workflow - open a file (or start a
 * new one), edit it full-screen with the cursor keys, save it back to
 * disk, search the text and cut/paste whole lines - on top of the
 * C128's 80-column (VDC) text mode.
 *
 * Split into modules:
 *   buffer.c/.h   - the in-memory document: one malloc'd line per row
 *   fileio.c/.h   - loading/saving the document via stdio
 *   input.c/.h    - line-editing prompts and Y/N confirmation, shared
 *                   by the save-as/search prompts and the exit check
 *   vblank.c/.h   - jiffy-clock throttle used before full-screen repaints
 *   screen.c/.h   - 80-column video mode setup and teardown
 *   editor.c/.h   - screen layout, key bindings and the edit loop
 */
#include "screen.h"
#include "buffer.h"
#include "editor.h"
#include <c128.h>

int main(void)
{
	fast();
    screen_init();
    buffer_init();

    editor_run();

    buffer_free_all();
    screen_shutdown();
    return 0;
}
