#include <conio.h>
#include <c128.h>
#include "contrast.h"
#include "ruler.h"
#include "input.h"

unsigned char contrast_demo(void)
{
    unsigned char quit;

    textcolor(COLOR_WHITE);
    cputsxy(0, 4, "Switching live to 40-column mode - the same ruler now");
    cputsxy(0, 5, "wraps after column 39 instead of reaching 79:");

    /* Switch to the VIC-II's 40-column screen and draw the ruler there. */
    videomode(VIDEOMODE_40COL);
    clrscr();
    cputsxy(0, 0, "40-column mode - press any key to switch back");
    ruler_draw(2);
    quit = input_wait_continue();

    /* Back to the VDC's 80 columns for the rest of the tour. */
    videomode(VIDEOMODE_80COL);
    clrscr();

    return quit;
}
