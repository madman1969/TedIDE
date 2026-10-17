#include <conio.h>
#include "input.h"

/* Q in either case, since the keyboard may be in shifted or unshifted mode. */
static unsigned char is_quit_key(char c)
{
    return c == 'q' || c == 'Q';
}

unsigned char input_quit_requested(void)
{
    /* kbhit() never waits; only read a key if one is already waiting. */
    if (kbhit())
        return is_quit_key(cgetc());
    return 0;
}

unsigned char input_wait_continue(void)
{
    return is_quit_key(cgetc());
}
