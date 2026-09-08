#include "vblank.h"

void wait_vblank(void)
{
    uint8_t old = JIFFY;

    while (JIFFY == old)
        ;
}