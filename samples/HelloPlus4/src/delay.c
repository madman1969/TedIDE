#include "delay.h"

void delay_short(void)
{
    /* volatile stops the compiler optimising the empty loop away. */
    volatile unsigned int i;
    for (i = 0; i < 6000; i++)
    {
        /* busy wait - cc65's default targets have no standard sleep() */
    }
}

void delay_long(void)
{
    /* Eight short delays back to back. */
    unsigned char reps;
    for (reps = 0; reps < 8; reps++)
    {
        delay_short();
    }
}
