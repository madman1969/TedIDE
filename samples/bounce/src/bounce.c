/*
 * Bounces a handful of characters ('@', 'A', 'B', ...) diagonally around
 * the screen, each reflecting off whichever edge it hits. Runs
 * indefinitely.
 */
#include <main.h>

typedef struct {
    int x, y;
    int dx, dy;
    char symbol;
} Sprite;

#define NUM_CHARS 5

int main(void)
{
    unsigned char w, h;
    Sprite s[NUM_CHARS];
    int d;
    int i;

    screensize(&w, &h);
    clrscr();
    textcolor(COLOR_BLACK);

    /* Initialise sprites */
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

        /* Delay */
        for (d = 0; d < 300; d++);
    }

    return 0;
}
