/*
 * Tedide sample: a tiny multi-file cc65/C64 program.
 *
 * Split across a few modules on purpose, to show off Tedide's Solution
 * Explorer and tabbed editor with more than a single main.c:
 *   screen.c/.h     - clear/print helpers built on conio.h
 *   animation.c/.h  - the bouncing character's state machine
 *   input.c/.h      - non-blocking keyboard polling
 *   delay.c/.h      - a crude busy-wait between animation steps
 */
#include "screen.h"
#include "animation.h"
#include "input.h"

int main(void)
{
    screen_init();

    while (!input_quit_requested())
    {
        animation_step();
    }

    screen_shutdown();
    return 0;
}
