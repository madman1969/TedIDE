/*
 * Tedide sample: a tour of the Commodore 128's 80-column (VDC-chip) text
 * mode, a feature unique to the C128 among cc65's Commodore targets - the
 * C64/Plus4/C16/VIC-20/PET are all fixed at 40 columns or fewer.
 * Split into modules, one per angle on the feature:
 *   ruler.c/.h     - screensize() + a column ruler proving 80 columns are real
 *   columns.c/.h   - two independent text columns laid out side by side
 *   contrast.c/.h  - switches live to 40 columns and back, for comparison
 *   screen.c/.h    - shared title banner / video mode setup and teardown
 *   input.c/.h     - keyboard polling shared by all three demos
 */
#include <conio.h>
#include "screen.h"
#include "ruler.h"
#include "columns.h"
#include "contrast.h"
#include "input.h"

typedef unsigned char (*demo_fn)(void);

static const demo_fn demos[] = { ruler_demo, columns_demo, contrast_demo };
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
