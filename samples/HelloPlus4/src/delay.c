#include "delay.h"

void delay_short(void)
{
    volatile unsigned int i;
    for (i = 0; i < 6000; i++)
    {
        /* busy wait - cc65's default targets have no standard sleep() */
    }
}

void delay_long(void)
{
    unsigned char reps;
    for (reps = 0; reps < 8; reps++)
    {
        delay_short();
    }
}
