#include <conio.h>
#include "input.h"

static unsigned char is_quit_key(char c)
{
    return c == 'q' || c == 'Q';
}

unsigned char input_quit_requested(void)
{
    if (kbhit())
        return is_quit_key(cgetc());
    return 0;
}

unsigned char input_wait_continue(void)
{
    return is_quit_key(cgetc());
}
