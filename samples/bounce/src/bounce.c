/*
 * Bounces a handful of characters ('@', 'A', 'B', ...) diagonally around
 * the screen, each reflecting off whichever edge it hits. Runs
 * indefinitely.
 */
#include <main.h>

/* One bouncing character: its position, its direction of travel on each
 * axis (+1 or -1), and the character to draw. */
typedef struct {
    int x, y;
    int dx, dy;
    char symbol;
} Sprite;

#define NUM_CHARS 8

int main(void)
{
    unsigned char w, h;
    Sprite s[NUM_CHARS];
    int d;
    int i;

    /* Ask conio for the screen size, so this works on 22-, 40- and 80-column machines. */
    screensize(&w, &h);
    clrscr();
    textcolor(COLOR_BLACK);

    /* Initialise sprites: spread out from the centre of the screen, each
     * heading off in one of the four diagonal directions. */
    for (i = 0; i < NUM_CHARS; i++) {
        s[i].x = (w / 2) + (i * 2);
        s[i].y = (h / 2) + (i * 1);
        s[i].dx = (i & 1) ? 1 : -1;
        s[i].dy = (i & 2) ? 1 : -1;
        s[i].symbol = '@' + i;   /* '@', 'A', 'B', 'C', ... */
    }

    while (1)
    {
        for (i = 0; i < NUM_CHARS; i++)
        {
            /* Each character gets its own colour, 0-7. */
        	textcolor(COLOR_BLACK + i);

            /* Erase old position */
            gotoxy(s[i].x, s[i].y);
            cputc(' ');

            /* Update position */
            s[i].x += s[i].dx;
            s[i].y += s[i].dy;

            /* Bounce horizontally */
            if (s[i].x <= 0) s[i].dx = 1;
            if (s[i].x >= w - 1) s[i].dx = -1;

            /* Bounce vertically */
            if (s[i].y <= 0) s[i].dy = 1;
            if (s[i].y >= h - 1) s[i].dy = -1;

            /* Draw new position */
            gotoxy(s[i].x, s[i].y);
            cputc(s[i].symbol);
        }

        /* Delay: a short busy-wait so the movement is visible. */
        for (d = 0; d < 300; d++);
    }

    return 0;
}
