#include <conio.h>
#include <c128.h>
#include <tgi.h>
#include "bitmap.h"
#include "vdc.h"
#include "input.h"

static const unsigned char palette[2] = { TGI_COLOR_BLACK, TGI_COLOR_LIGHTGREEN };

static const unsigned char bar_heights[8] = { 60, 110, 85, 150, 125, 170, 95, 140 };

static void draw_picture(void)
{
    int maxx, maxy;
    unsigned char i, radius;

    maxx = tgi_getmaxx();
    maxy = tgi_getmaxy();

    /* Frame */
    tgi_line(0, 0, maxx, 0);
    tgi_lineto(maxx, maxy);
    tgi_lineto(0, maxy);
    tgi_lineto(0, 0);

    /* Left: a fan of lines from the bottom-left corner. */
    for (i = 0; i <= 20; ++i)
        tgi_line(1, maxy - 1, 10 + i * 10, 1);

    /* Middle: a bar chart of filled boxes. */
    for (i = 0; i < 8; ++i)
        tgi_bar(230 + i * 24, maxy - 10 - bar_heights[i], 246 + i * 24, maxy - 10);

    /* Right: concentric circles (aspect-corrected by TGI) inside an ellipse. */
    for (radius = 10; radius <= 90; radius += 16)
        tgi_circle(530, maxy / 2, radius);
    tgi_ellipse(530, maxy / 2, 105, 90);
}

unsigned char bitmap_demo(void)
{
    unsigned char blank_attr, quit;

    textcolor(COLOR_WHITE);
    cputsxy(0, 4, "Bit 7 of register 25 turns the VDC into a 640x200 bitmap - twice the");
    cputsxy(0, 5, "width of the VIC-II's hi-res screen. cc65's c128-vdc TGI driver draws on it.");
    cputsxy(0, 7, "Any key: switch to bitmap mode (any key again comes back)   Q: quit");

    if (input_wait_continue())
        return DEMO_QUIT;

    /* The bitmap overwrites screen and attribute RAM, so remember what a blank cell looked like. */
    blank_attr = vdc_get_attr(VDC_COLUMNS - 1, VDC_ROWS - 2);

    tgi_install(c128_vdc_tgi);
    if (tgi_geterror() != TGI_ERR_OK)
    {
        cputsxy(0, 9, "Couldn't start the VDC bitmap driver.");
        return DEMO_CONTINUE;
    }

    tgi_init();
    tgi_setpalette(palette);
    tgi_clear();
    tgi_setcolor(1);
    draw_picture();

    quit = input_wait_continue();

    /* The driver restores text mode and reloads the ROM character set. */
    tgi_uninstall();
    vdc_fill(vdc_attr_base(), blank_attr, VDC_COLUMNS * VDC_ROWS);
    clrscr();

    return quit ? DEMO_QUIT : DEMO_NEXT;
}
