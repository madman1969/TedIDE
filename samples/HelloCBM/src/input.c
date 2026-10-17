#include <conio.h>
#include "input.h"

unsigned char input_quit_requested(void)
{
    /* kbhit() only checks the keyboard buffer, so the animation never
     * stops to wait; cgetc() then takes the waiting key out of it. */
    if (kbhit())
    {
        char c = cgetc();
        if (c == 'q' || c == 'Q')
            return 1;
    }
    return 0;
}
