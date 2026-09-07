#include <conio.h>
#include <time.h>
/* See palette.c for why this is included explicitly - fast()/slow()/
 * isfast() are declared in <cbm264.h>, pulled in through here. */
#include <plus4.h>
#include "speed.h"
#include "input.h"

#define OUTER_ITERATIONS 40
#define INNER_ITERATIONS 2000

/* A fixed amount of work, timed with the jiffy clock rather than counted
 * against a fixed time budget - clock() keeps ticking at the same
 * real-time rate under fast() as it does under slow(), since it's driven
 * by TED's raster timing, not the CPU clock that fast()/slow() doubles. */
static void busy_loop(void)
{
    unsigned char outer;
    volatile unsigned int inner;
    for (outer = 0; outer < OUTER_ITERATIONS; outer++)
    {
        for (inner = 0; inner < INNER_ITERATIONS; inner++)
        {
            /* deliberately empty - just burning cycles for the benchmark */
        }
    }
}

unsigned char speed_demo(void)
{
    clock_t slow_ticks, fast_ticks;

    textcolor(COLOR_WHITE);
    cputsxy(0, 4, "TED lets the CPU run at double its normal clock speed -");
    cputsxy(0, 5, "timing the same busy loop at both speeds:");

    slow_ticks = clock();
    busy_loop();
    slow_ticks = clock() - slow_ticks;

    fast();
    fast_ticks = clock();
    busy_loop();
    fast_ticks = clock() - fast_ticks;
    slow();

    gotoxy(0, 7);
    cprintf("normal speed: %lu jiffies\r\n", (unsigned long)slow_ticks);
    cprintf("fast  speed:  %lu jiffies\r\n", (unsigned long)fast_ticks);
    cprintf("isfast() now reports %u\r\n", (unsigned)isfast());

    return input_quit_requested();
}
