/*
 * Tedide sample: a tour of Commodore Plus/4-only cc65 features, unlike
 * HelloC64 next door, which deliberately builds for every Commodore target.
 * Split into modules the same way, one per TED party trick:
 *   palette.c/.h  - the TED chip's 121-color palette (16 hues x 8 luma)
 *   sound.c/.h    - TED's two sound voices, one with a dedicated noise mode
 *   speed.c/.h    - fast()/slow(), the C16/Plus4's CPU clock-doubling switch
 *   screen.c/.h   - shared title banner / cursor setup
 *   input.c/.h    - keyboard polling shared by all three demos
 */
#include <conio.h>
#include "screen.h"
#include "palette.h"
#include "sound.h"
#include "speed.h"
#include "input.h"

typedef unsigned char (*demo_fn)(void);

static const demo_fn demos[] = { palette_demo, sound_demo, speed_demo };
#define DEMO_COUNT (sizeof(demos) / sizeof(demos[0]))

static unsigned char run_stage(demo_fn demo)
{
    unsigned char quit;

    clrscr();
    screen_banner();
    quit = demo();

    if (!quit)
    {
        gotoxy(0, 23);
        cputs("Any key: next demo   Q: quit");
        quit = input_wait_continue();
    }

    return quit;
}

int main(void)
{
    unsigned char quit = 0;
    unsigned char i;

    screen_init();

    while (!quit)
    {
        for (i = 0; i < DEMO_COUNT && !quit; i++)
        {
            quit = run_stage(demos[i]);
        }
    }

    screen_shutdown();
    return 0;
}
