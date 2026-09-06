#include <conio.h>
#include "input.h"

unsigned char input_quit_requested(void)
{
    if (kbhit())
    {
        char c = cgetc();
        if (c == 'q' || c == 'Q')
            return 1;
    }
    return 0;
}
