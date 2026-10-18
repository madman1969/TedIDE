/*
** farwin.c - the window cache: FAR_WINDOWS buffers of ordinary memory, each a copy of a range of
** far memory, replaced least recently used first. Every other access writes back (and, if it
** changes far memory, drops) the windows it overlaps, so both views stay the same - through
** fm_sync, which far_map sets, so programs that never map a window don't link any of this.
*/

#include "farint.h"

typedef struct {
    farptr base;            /* the far address the window's first byte came from */
    unsigned len;           /* 0: unused */
    unsigned char dirty;    /* changed since it was read, so it must be written back */
    unsigned char age;      /* the tick it was last used on, for choosing which to replace */
} window;

static window windows[FAR_WINDOWS];
static unsigned char window_data[FAR_WINDOWS][FAR_WINDOW_SIZE];
static unsigned char tick;                  /* counts up on every far_map */
static unsigned char last_hit;              /* the window far_map returned last */

/* Copies window i back to far memory if it has been changed. */
static void write_back (unsigned char i)
{
    if (windows[i].dirty) {
        fm_write (windows[i].base, window_data[i], windows[i].len);
        windows[i].dirty = 0;
    }
}

/* Writes back any changed window overlapping [a, a + len), and drops them too if drop is set.
** Without drop, an unchanged window needs nothing, so it's passed over before the comparisons of
** longs, which are slow on a 6502. far_done drops them all this way. */
static void sync_range (farptr a, unsigned long len, unsigned char drop)
{
    unsigned char i;
    for (i = 0; i < FAR_WINDOWS; ++i) {
        if (windows[i].len == 0 || (!drop && !windows[i].dirty)) {
            continue;
        }
        if (a < windows[i].base + windows[i].len && windows[i].base < a + len) {
            write_back (i);
            if (drop) {
                windows[i].len = 0;
            }
        }
    }
}

void* far_map (farptr p, unsigned len, unsigned char mode)
{
    unsigned char i;
    unsigned char victim;
    window* w = &windows[last_hit];

    /* From now on the core's transfers keep the windows in line. */
    fm_sync = sync_range;

    /* The range must fit in one window and lie inside far memory. */
    if (len == 0 || len > FAR_WINDOW_SIZE || p + len > fm_size) {
        return 0;
    }

    /* The last window used is the likeliest - checked first, the rest only on a miss. */
    if (!(w->len && p >= w->base && p + len <= w->base + w->len)) {
        w = 0;
        for (i = 0; i < FAR_WINDOWS; ++i) {
            if (windows[i].len && p >= windows[i].base && p + len <= windows[i].base + windows[i].len) {
                w = &windows[i];
                last_hit = i;
                break;
            }
        }
    }

    if (!w) {
        /* A miss: the oldest window (or an unused one) takes the range starting at p. */
        victim = 0;
        for (i = 0; i < FAR_WINDOWS; ++i) {
            if (!windows[i].len) {
                victim = i;
                break;
            }
            /* Ages are compared as distances back from tick, so the counter can wrap. */
            if ((unsigned char) (tick - windows[i].age) > (unsigned char) (tick - windows[victim].age)) {
                victim = i;
            }
        }
        w = &windows[victim];
        write_back (victim);
        w->len = 0;
        /* Read a whole window's worth (less at the end of far memory), so nearby accesses hit. */
        len = (fm_size - p) < FAR_WINDOW_SIZE ? (unsigned) (fm_size - p) : FAR_WINDOW_SIZE;
        /* No two windows may hold the same byte, or one's changes would hide the other's. */
        sync_range (p, len, 1);
        fm_read (window_data[victim], p, len);
        w->base = p;
        w->len = len;
        w->dirty = 0;
        last_hit = victim;
    }

    /* Mark it as just used, and as changed if the caller is going to write to it. */
    w->age = ++tick;
    if (mode == FAR_WRITE) {
        w->dirty = 1;
    }
    return window_data[last_hit] + (unsigned) (p - w->base);
}

void far_commit (void)
{
    unsigned char i;
    for (i = 0; i < FAR_WINDOWS; ++i) {
        write_back (i);
    }
}

/* The single-byte and word accessors below map just the bytes they need; a bad address reads
** as 0 and writes nowhere. */

unsigned char far_peek (farptr p)
{
    unsigned char* b = far_map (p, 1, FAR_READ);
    return b ? *b : 0;
}

void far_poke (farptr p, unsigned char value)
{
    unsigned char* b = far_map (p, 1, FAR_WRITE);
    if (b) {
        *b = value;
    }
}

unsigned far_peekw (farptr p)
{
    unsigned* w = far_map (p, 2, FAR_READ);
    return w ? *w : 0;
}

void far_pokew (farptr p, unsigned value)
{
    unsigned* w = far_map (p, 2, FAR_WRITE);
    if (w) {
        *w = value;
    }
}
