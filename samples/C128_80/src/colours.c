#include <conio.h>
#include "colours.h"
#include "vdc.h"
#include "input.h"

#define BAR_WIDTH   5
#define BAR_TOP     7
#define BAR_HEIGHT  6
#define LEGEND_TOP  15

static const char* const colour_names[16] = {
    "Black",       "Dark grey",    "Dark blue",   "Light blue",
    "Dark green",  "Light green",  "Dark cyan",   "Light cyan",
    "Dark red",    "Light red",    "Dark purple", "Light purple",
    "Dark yellow", "Light yellow", "Light grey",  "White",
};

unsigned char colours_demo(void)
{
    unsigned char charset, colour, row, column;

    textcolor(COLOR_WHITE);
    cputsxy(0, 4, "16 RGBI colours, set cell by cell in the VDC's attribute RAM. The bars are");
    cputsxy(0, 5, "filled by the VDC's block-fill hardware (registers 30 and 31), not the CPU:");

    charset = vdc_text_charset();

    /* A reversed space is a solid block in the cell's colour. */
    for (colour = 0; colour < 16; ++colour)
    {
        for (row = BAR_TOP; row < BAR_TOP + BAR_HEIGHT; ++row)
            vdc_set_attrs(colour * BAR_WIDTH, row, charset | VDC_ATTR_REVERSE | colour, BAR_WIDTH);

        gotoxy(colour * BAR_WIDTH + 1, BAR_TOP + BAR_HEIGHT);
        cprintf("%2u", colour);
    }

    for (colour = 0; colour < 16; ++colour)
    {
        column = (colour / 4) * 20;
        row = LEGEND_TOP + (colour % 4);
        vdc_set_attrs(column, row, charset | VDC_ATTR_REVERSE | colour, 2);
        cputsxy(column + 3, row, colour_names[colour]);
    }

    return input_quit_requested();
}
