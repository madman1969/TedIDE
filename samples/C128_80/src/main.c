/*
 * Tedide sample: a tour of the Commodore 128's VDC (8563) chip - its
 * 80-column text mode and graphics, unique to the C128 among cc65's
 * Commodore targets: the C64/Plus4/C16/VIC-20/PET are all fixed at 40
 * columns or fewer. Split into modules, one per feature:
 *   ruler.c/.h      - screensize() + a column ruler proving 80 columns are real
 *   columns.c/.h    - two independent text columns laid out side by side
 *   colours.c/.h    - the 16 RGBI colours, drawn with the VDC's block fill
 *   attributes.c/.h - blink, underline, reverse and alternate-charset cells
 *   charset.c/.h    - redefined characters and a one-glyph animation
 *   effects.c/.h    - hardware smooth scrolling and whole-screen reverse
 *   bitmap.c/.h     - 640x200 bitmap graphics through cc65's TGI driver
 *   contrast.c/.h   - switches live to 40 columns and back, for comparison
 *   vdc.c/.h        - direct VDC register and RAM access shared by the above
 *   screen.c/.h     - shared title banner / video mode setup and teardown
 *   input.c/.h      - keyboard polling shared by all the demos
 */
#include <conio.h>
#include "screen.h"
#include "ruler.h"
#include "columns.h"
#include "colours.h"
#include "attributes.h"
#include "charset.h"
#include "effects.h"
#include "bitmap.h"
#include "contrast.h"
#include "input.h"

/* Every demo has the same shape - no arguments, returns a DEMO_ value from
 * input.h - so they can sit in one table and be run in turn. */
typedef unsigned char (*demo_fn)(void);

static const demo_fn demos[] = {
    ruler_demo, columns_demo, colours_demo, attributes_demo,
    charset_demo, effects_demo, bitmap_demo, contrast_demo,
};
#define DEMO_COUNT (sizeof(demos) / sizeof(demos[0]))

/* Runs one demo on a fresh screen and, unless it already waited for its own
 * key, prompts and waits for one. Returns 1 if Q was pressed. */
static unsigned char run_stage(demo_fn demo)
{
    unsigned char result;

    clrscr();
    screen_banner();
    result = demo();

    if (result == DEMO_CONTINUE)
    {
        screen_prompt();
        result = input_wait_continue();
    }

    return result == DEMO_QUIT;
}

int main(void)
{
    unsigned char quit = 0;
    unsigned char i;

    screen_init();

    /* Loop through the demos, starting again after the last, until Q. */
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
