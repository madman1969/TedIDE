#include <conio.h>
#include "effects.h"
#include "vdc.h"
#include "ruler.h"
#include "input.h"
#include "screen.h"

/* Fine-scroll offsets (0-7 pixels) tracing a circle; vertical uses the same
 * table a quarter-turn later. */
static const unsigned char circle[16] = { 4, 5, 6, 7, 7, 7, 6, 5, 4, 3, 2, 1, 1, 1, 2, 3 };

unsigned char effects_demo(void)
{
    unsigned char saved_h, saved_v, h_bits, v_bits, frame, step, row;

    textcolor(COLOR_WHITE);
    cputsxy(0, 4, "Hardware smooth scrolling: registers 25 (horizontal) and 24 (vertical) move");
    cputsxy(0, 5, "the whole display a pixel at a time, once per vertical blank. Bit 6 of");
    cputsxy(0, 6, "register 24 reverses every cell at once - watch for it every few seconds.");

    for (row = 9; row < 21; row += 2)
    {
        ruler_draw(row);
        cputsxy(0, row + 1, "<<< VDC smooth scroll >>> <<< VDC smooth scroll >>> <<< VDC smooth scroll >>>");
    }
    screen_prompt();

    saved_h = vdc_read_reg(VDC_REG_HSCROLL);
    saved_v = vdc_read_reg(VDC_REG_VSCROLL);
    h_bits = saved_h & 0xF0;
    v_bits = saved_v & (0xE0 & ~VDC_VSCROLL_COPY);

    frame = 0;
    while (!kbhit())
    {
        vdc_wait_vblank();
        ++frame;
        if (frame == 0)
            v_bits ^= VDC_VSCROLL_REVERSE;

        step = frame >> 1;
        vdc_write_reg(VDC_REG_HSCROLL, h_bits | circle[step & 15]);
        vdc_write_reg(VDC_REG_VSCROLL, v_bits | circle[(step + 4) & 15]);
    }

    vdc_write_reg(VDC_REG_HSCROLL, saved_h);
    vdc_write_reg(VDC_REG_VSCROLL, saved_v);
    return input_wait_next();
}
