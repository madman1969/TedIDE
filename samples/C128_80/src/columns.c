#include <conio.h>
#include "columns.h"
#include "input.h"

static const char* const left_column[] = {
    "MENU",
    "1) New Game",
    "2) Load Game",
    "3) High Scores",
    "4) Quit",
};

static const char* const right_column[] = {
    "DESCRIPTION",
    "Start a fresh adventure",
    "Continue a saved one",
    "See who's done best",
    "Back to BASIC",
};

#define ROW_COUNT (sizeof(left_column) / sizeof(left_column[0]))

unsigned char columns_demo(void)
{
    unsigned char i;

    textcolor(COLOR_WHITE);
    cputsxy(0, 4, "Two independent columns, 40 characters apart - only");
    cputsxy(0, 5, "possible without overlap because the screen is 80 wide:");

    for (i = 0; i < ROW_COUNT; i++)
    {
        cputsxy(0, 7 + i, left_column[i]);
        cputsxy(40, 7 + i, right_column[i]);
    }

    return input_quit_requested();
}
