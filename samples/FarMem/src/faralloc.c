/*
** faralloc.c - the allocator. Far memory is a chain of blocks, each starting with a 4-byte header:
** the block's whole size (a multiple of 4), with bit 0 set while it's in use. Free neighbours are
** merged as a search passes them, and a search starts where the last one ended ("next fit"), so
** repeated allocations don't rescan the full blocks at the bottom. far_init writes the first
** header: one free block covering everything.
*/

#include "farint.h"

#define HEADER 4UL
#define USED   1UL

/* Reads and writes the 4-byte header at the start of a block. */
static unsigned long header_at (farptr block)
{
    unsigned long h;
    far_read (&h, block, sizeof h);
    return h;
}

static void set_header (farptr block, unsigned long h)
{
    far_write (block, &h, sizeof h);
}

/* Merges the free blocks after this free one into it; returns its new size. */
static unsigned long merge_following (farptr block, unsigned long bsize)
{
    farptr next;
    unsigned long h;
    unsigned long merged = bsize;

    for (next = block + bsize; next < fm_heap_end; next += h) {
        h = header_at (next);
        if (h & USED) {
            break;
        }
        /* The rover can't be left pointing into the middle of the merged block. */
        if (next == fm_rover) {
            fm_rover = block;
        }
        merged += h;
    }
    if (merged != bsize) {
        set_header (block, merged);
    }
    return merged;
}

farptr far_alloc (unsigned long n)
{
    farptr block;
    farptr start;
    unsigned long h;
    unsigned long need;
    unsigned char wrapped = 0;

    if (n == 0 || n > fm_size) {
        return FAR_NULL;
    }
    /* The block's whole size: the request plus its header, rounded up to a multiple of 4 so
    ** bit 0 of every size is free for the in-use flag. */
    need = (n + HEADER + 3) & ~3UL;

    /* Walk the chain from the rover to the end, then from 0 back up to the rover. */
    start = fm_rover;
    block = fm_rover;
    for (;;) {
        if (block >= fm_heap_end) {
            if (wrapped) {
                break;
            }
            wrapped = 1;
            block = 0;
        }
        if (wrapped && block >= start) {
            break;
        }
        h = header_at (block);
        if ((h & ~USED) == 0) {
            return FAR_NULL;            /* a damaged chain: never loop on it */
        }
        if (!(h & USED)) {
            h = merge_following (block, h);
            if (h >= need) {
                /* Big enough. Split off the rest as a new free block, unless it would be too
                ** small to hold anything. */
                if (h - need >= HEADER + 4) {
                    set_header (block + need, h - need);
                    h = need;
                }
                set_header (block, h | USED);
                fm_rover = block;
                return block + HEADER;          /* the caller's bytes start after the header */
            }
        }
        block += h & ~USED;
    }
    return FAR_NULL;
}

void far_free (farptr p)
{
    farptr block = p - HEADER;
    unsigned long h;

    /* Ignore pointers that can't be blocks, and blocks already free. */
    if (p < HEADER || p >= fm_heap_end) {
        return;
    }
    h = header_at (block);
    if (!(h & USED)) {
        return;
    }
    /* Clear the in-use bit, and merge any free blocks after it straight away. */
    h &= ~USED;
    set_header (block, h);
    merge_following (block, h);
}

/* Walks the whole chain, merging free neighbours, and returns the largest free block's usable size. */
unsigned long far_maxavail (void)
{
    farptr block;
    unsigned long h;
    unsigned long best = 0;

    for (block = 0; block < fm_heap_end; block += h & ~USED) {
        h = header_at (block);
        if (!(h & USED)) {
            h = merge_following (block, h);
            if (h - HEADER > best) {
                best = h - HEADER;
            }
        }
    }
    return best;
}
