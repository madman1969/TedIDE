#include "delay.h"

void delay_short(void)
{
    /* volatile stops the compiler optimising the empty loop away. */
    volatile unsigned int i;
    for (i = 0; i < 600; i++)
    {
        /* busy wait - cc65's default C64 target has no standard sleep() */
    }

}
