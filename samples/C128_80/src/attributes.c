#include <conio.h>
#include <string.h>
#include "attributes.h"
#include "vdc.h"
#include "input.h"

#define SAMPLE_COLUMN 34

typedef struct
{
    const char* label;
    const char* sample;
    unsigned char attr;   /* attribute bits; the charset bit is XORed with the text's */
} attribute_line;

static const attribute_line lines[] = {
    { "Bit 4 - blink:",             "Blinking text",            VDC_ATTR_BLINK | VDC_LIGHT_CYAN },
    { "Bit 5 - underline:",         "Underlined text",          VDC_ATTR_UNDERLINE | VDC_LIGHT_GREEN },
    { "Bit 6 - reverse:",           "Reversed text",            VDC_ATTR_REVERSE | VDC_LIGHT_YELLOW },
    { "Bit 7 - alternate charset:", "Same bytes, other glyphs", VDC_ATTR_ALTCHARSET | VDC_LIGHT_PURPLE },
    { "All four bits together:",    "Everything at once",
      VDC_ATTR_BLINK | VDC_ATTR_UNDERLINE | VDC_ATTR_REVERSE | VDC_ATTR_ALTCHARSET | VDC_LIGHT_RED },
};

#define LINE_COUNT (sizeof(lines) / sizeof(lines[0]))

unsigned char attributes_demo(void)
{
    unsigned char charset, i, row;

    textcolor(COLOR_WHITE);
    cputsxy(0, 4, "Each cell's attribute byte holds a colour in bits 0-3 and four display");
    cputsxy(0, 5, "attributes in bits 4-7, all applied by the VDC itself:");

    charset = vdc_text_charset();

    for (i = 0; i < LINE_COUNT; ++i)
    {
        row = 7 + i * 2;
        cputsxy(0, row, lines[i].label);
        cputsxy(SAMPLE_COLUMN, row, lines[i].sample);
        vdc_set_attrs(SAMPLE_COLUMN, row, lines[i].attr ^ charset, strlen(lines[i].sample));
    }

    return input_quit_requested();
}
