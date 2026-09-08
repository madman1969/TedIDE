#include <stdlib.h>
#include <string.h>
#include "buffer.h"

static char *lines[BUF_MAX_LINES];
static unsigned int line_count;
static unsigned char modified;

void buffer_init(void)
{
    line_count = 0;
    modified = 0;
    buffer_insert_line(0, "");
    modified = 0;
}

void buffer_free_all(void)
{
    unsigned int i;

    for (i = 0; i < line_count; i++)
        free(lines[i]);
    line_count = 0;
}

unsigned int buffer_line_count(void)
{
    return line_count;
}

unsigned char buffer_line_len(unsigned int line)
{
    return (unsigned char)strlen(lines[line]);
}

const char *buffer_line_text(unsigned int line)
{
    return lines[line];
}

unsigned char buffer_modified(void)
{
    return modified;
}

void buffer_set_modified(unsigned char flag)
{
    modified = flag;
}

unsigned char buffer_insert_line(unsigned int at, const char *text)
{
    unsigned int i;
    char *dup;

    if (line_count >= BUF_MAX_LINES)
        return 0;

    dup = malloc(strlen(text) + 1);
    if (!dup)
        return 0;
    strcpy(dup, text);

    for (i = line_count; i > at; i--)
        lines[i] = lines[i - 1];
    lines[at] = dup;
    line_count++;
    modified = 1;
    return 1;
}

void buffer_delete_line(unsigned int at)
{
    unsigned int i;

    free(lines[at]);
    for (i = at; i < line_count - 1; i++)
        lines[i] = lines[i + 1];
    line_count--;
    modified = 1;
}

unsigned char buffer_split_line(unsigned int line, unsigned char col)
{
    char *s = lines[line];
    unsigned char len = (unsigned char)strlen(s);
    char *tail;
    char *shrunk;

    if (line_count >= BUF_MAX_LINES)
        return 0;

    tail = malloc((unsigned char)(len - col) + 1);
    if (!tail)
        return 0;
    memcpy(tail, s + col, (unsigned char)(len - col));
    tail[len - col] = '\0';

    if (!buffer_insert_line(line + 1, tail))
    {
        free(tail);
        return 0;
    }
    free(tail);

    shrunk = realloc(s, (unsigned char)(col + 1));
    if (shrunk)
        lines[line] = shrunk;
    lines[line][col] = '\0';
    modified = 1;
    return 1;
}

unsigned char buffer_join_next_line(unsigned int line)
{
    char *a = lines[line];
    char *b = lines[line + 1];
    unsigned char la = (unsigned char)strlen(a);
    unsigned char lb = (unsigned char)strlen(b);
    char *grown;

    if ((unsigned int)la + lb > BUF_MAX_LINE_LEN)
        return 0;

    grown = realloc(a, (unsigned char)(la + lb) + 1);
    if (!grown)
        return 0;
    memcpy(grown + la, b, (unsigned char)(lb + 1));
    lines[line] = grown;

    buffer_delete_line(line + 1);
    modified = 1;
    return 1;
}

unsigned char buffer_insert_char(unsigned int line, unsigned char col, unsigned char c)
{
    char *old = lines[line];
    unsigned char len = (unsigned char)strlen(old);
    char *grown;

    if (len >= BUF_MAX_LINE_LEN)
        return 0;

    grown = realloc(old, (unsigned char)(len + 2));
    if (!grown)
        return 0;

    memmove(grown + col + 1, grown + col, (unsigned char)(len - col) + 1);
    grown[col] = c;
    lines[line] = grown;
    modified = 1;
    return 1;
}

unsigned char buffer_delete_char(unsigned int line, unsigned char col)
{
    char *s = lines[line];
    unsigned char len = (unsigned char)strlen(s);
    char *shrunk;

    if (col >= len)
        return 0;

    memmove(s + col, s + col + 1, (unsigned char)(len - col));
    shrunk = realloc(s, len);
    if (shrunk)
        lines[line] = shrunk;
    modified = 1;
    return 1;
}
