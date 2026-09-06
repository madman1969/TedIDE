#include <conio.h>
#include "animation.h"
#include "screen.h"
#include "delay.h"
#include "border.h"

#define ROW      12
#define MIN_COL  0
#define MAX_COL  39

static unsigned char pos   = MIN_COL;
static signed char   step  = 1;
static unsigned char color = COLOR_YELLOW;

void animation_step(void)
{
    screen_put_char(pos, ROW, ' ', COLOR_BLACK);

    if (pos == MAX_COL)
    {
        step = -1;
        color = (color % 15) + 1;
    }
    else if (pos == MIN_COL)
    {
        step = 1;
        color = (color % 15) + 1;
    }
    pos = (unsigned char)(pos + step);

    screen_put_char(pos, ROW, '*', color);
    delay_short();
    border_flash();
}
