#include <conio.h>
#include "animation.h"
#include "screen.h"
#include "delay.h"
#include "border.h"

#define ROW      12
#define MIN_COL  0
#define MAX_COL  39

/* PET and the CBM-II "B" series (610/620/710/720) have monochrome, text-only
 * CRTC video - conio.h only defines COLOR_BLACK/COLOR_WHITE for them (see
 * pet.h/cbm610.h), not the full 16-color palette every other CBM target has.
 * The bouncing character just stays white there instead of cycling colors. */
#if defined(__PET__) || defined(__CBM610__)
#define ANIMATION_COLOR_CYCLING 0
#else
#define ANIMATION_COLOR_CYCLING 1
#endif

static unsigned char pos   = MIN_COL;
static signed char   step  = 1;
#if ANIMATION_COLOR_CYCLING
static unsigned char color = COLOR_YELLOW;
#else
static unsigned char color = COLOR_WHITE;
#endif

void animation_step(void)
{
    screen_put_char(pos, ROW, ' ', COLOR_BLACK);

    if (pos == MAX_COL)
    {
        step = -1;
#if ANIMATION_COLOR_CYCLING
        color = (color % 15) + 1;
#endif
    }
    else if (pos == MIN_COL)
    {
        step = 1;
#if ANIMATION_COLOR_CYCLING
        color = (color % 15) + 1;
#endif
    }
    pos = (unsigned char)(pos + step);

    screen_put_char(pos, ROW, '*', color);
    delay_short();
    border_flash();
}
