#include <conio.h>
#include <cbm.h>
#include "input.h"

unsigned char input_line(unsigned char row, unsigned char col, char *buf, unsigned char maxlen)
{
    unsigned char len = 0;
    unsigned char c;

    buf[0] = '\0';
    gotoxy(col, row);

    for (;;)
    {
        c = cgetc();

        if (c == CH_ENTER)
        {
            buf[len] = '\0';
            return 1;
        }
        else if (c == CH_ESC || c == CH_STOP)
        {
            return 0;
        }
        else if (c == CH_DEL)
        {
            if (len > 0)
            {
                len--;
                gotoxy(col + len, row);
                cputc(' ');
                gotoxy(col + len, row);
            }
        }
        else if (c >= 32 && c < 127 && len < maxlen)
        {
            buf[len++] = c;
            cputc(c);
        }
    }
}

unsigned char input_confirm(void)
{
    unsigned char c;

    for (;;)
    {
        c = cgetc();
        if (c == 'y' || c == 'Y')
            return CONFIRM_YES;
        if (c == 'n' || c == 'N')
            return CONFIRM_NO;
        if (c == CH_ESC || c == CH_STOP)
            return CONFIRM_CANCEL;
    }
}
