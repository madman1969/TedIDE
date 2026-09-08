#include <stdio.h>
#include <string.h>
#include "fileio.h"
#include "buffer.h"

/* Room for a full line plus the newline fgets() leaves in and the NUL. */
#define LINE_INPUT_MAX (BUF_MAX_LINE_LEN + 2)

unsigned char fileio_load(const char *filename)
{
    static char line[LINE_INPUT_MAX];
    FILE *fp;
    unsigned char any = 0;
    size_t len;

    fp = fopen(filename, "r");
    if (!fp)
        return 0;

    buffer_free_all();

    while (fgets(line, sizeof(line), fp))
    {
        len = strlen(line);
        while (len && (line[len - 1] == '\n' || line[len - 1] == '\r'))
            line[--len] = '\0';
        buffer_insert_line(buffer_line_count(), line);
        any = 1;
    }
    fclose(fp);

    if (!any)
        buffer_insert_line(0, "");

    buffer_set_modified(0);
    return 1;
}

unsigned char fileio_save(const char *filename)
{
    FILE *fp;
    unsigned int i, count;

    fp = fopen(filename, "w");
    if (!fp)
        return 0;

    count = buffer_line_count();
    for (i = 0; i < count; i++)
    {
        fputs(buffer_line_text(i), fp);
        fputc('\n', fp);
    }

    fclose(fp);
    buffer_set_modified(0);
    return 1;
}
