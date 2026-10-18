/*
** farmem.c - far memory: backend selection, transfers split to what each backend can do in one
** go, the window cache, and a first-fit allocator whose block headers live in far memory itself.
*/

#include <stdlib.h>
#include <string.h>
#include <em.h>
#include "farmem.h"

#if defined(__C64__)
#  include <c64.h>
#  define HAS_REU   1
#  define HAS_SCPU  1
#elif defined(__C128__)
#  include <c128.h>
#  define HAS_REU   1
#  define HAS_EMD   1
#elif defined(__C16__) || defined(__PLUS4__)
#  define HAS_TEDX  1           /* Hannes and Csory RAM expansions */
#  if !defined(__PLUS4__)
#    include <c16.h>
#    define HAS_EMD 1           /* cc65's c16-ram driver; its Plus/4 target has none */
#  endif
#elif defined(__CBM510__)
#  include <cbm510.h>
#  define HAS_CBM   1
#  define CBM_FIRST_BANK 1      /* the program runs in bank 0 */
#elif defined(__CBM610__)
#  include <cbm610.h>
#  define HAS_CBM   1
#  define CBM_FIRST_BANK 2      /* the program runs in bank 1 */
#endif

/* farhw.s - the assembly routines take their arguments from these variables rather than the C
** stack: the near (ordinary memory) address, the far source and destination, the length, and
** extra REU control bits. */
extern void* far_hw_near;
extern unsigned long far_hw_src;
extern unsigned long far_hw_dst;
extern unsigned far_hw_len;
extern unsigned char far_hw_ctrl;
#if HAS_REU
void __fastcall__ reu_go (unsigned char command);
/* REU command register values that start a transfer: C64 to REU (stash) or REU to C64 (fetch). */
#  define REU_STASH 0x80
#  define REU_FETCH 0x81
#endif
#if HAS_SCPU
void __fastcall__ scpu_move (unsigned char backward);
#endif
#if HAS_CBM
void cbm_fetch (void);
void cbm_stash (void);
#endif
#if HAS_TEDX
void __fastcall__ tedx_copy (unsigned char stash);
unsigned char __fastcall__ tedx_count (unsigned char banks);
extern unsigned char tedx_select[16];       /* the register value for each bank */
#endif

#define BOUNCE_SIZE 256

/* The program's copy hook (far_set_copy_hook), called before each copy with interrupts off. */
static void (*copy_hook) (void);

static void run_copy_hook (void)
{
    if (copy_hook) {
        copy_hook ();
    }
}

void far_set_copy_hook (void (*hook) (void))
{
    copy_hook = hook;
}

static unsigned char backend;               /* a FAR_ value, chosen by far_init */
static unsigned long size;                  /* bytes of far memory */
static unsigned char* near_base;            /* FAR_NEAR's block */
static unsigned char bounce[BOUNCE_SIZE];   /* far-to-far copies and fills */
#if HAS_SCPU
static unsigned long scpu_base;             /* far offset 0 as a 65816 address */
#endif
#if HAS_EMD
static struct em_copy request;              /* reused for every extended memory driver call */
#endif
#if HAS_CBM
static unsigned char cbm_banks;             /* how many RAM banks far_init found */
#endif
#if HAS_TEDX
static unsigned char tedx_pages;            /* the pages of each bank used, from $4000 */
static const char* tedx_name;               /* which board far_init found */
#endif

/* ------------------------------------------------------------------------
** The backends. Each moves any length; callers never pass 0.
*/

/* How much of len fits before a crosses a 64K boundary - the most one REU or SuperCPU transfer
** can move, since neither carries into the bank byte reliably. */
#if HAS_REU || HAS_SCPU
static unsigned to_bank_end (unsigned long a, unsigned len)
{
    /* 0 - low is the distance to the next 64K boundary (0 meaning a whole bank). */
    unsigned low = (unsigned) a;
    if (low != 0 && (unsigned) (0 - low) < len) {
        return 0 - low;
    }
    return len;
}
#endif

#if HAS_CBM
/* Points far_hw_src at far address a - 255 pages per bank, from page 1, as the 6509 keeps its
** bank registers at $0000/$0001 in every bank - and returns how many bytes are left in that bank. */
static unsigned cbm_locate (unsigned long a)
{
    unsigned page = (unsigned) (a >> 8);
    unsigned char bank = page / 255;
    unsigned char inbank = page % 255;
    far_hw_src = ((unsigned long) (CBM_FIRST_BANK + bank) << 16)
        | ((unsigned) (inbank + 1) << 8) | (unsigned char) a;
    return ((unsigned) (255 - inbank) << 8) - (unsigned char) a;
}
#endif

#if HAS_TEDX
/* Points far_hw_src at far address a - tedx_pages pages per bank, from $4000 in the CPU's window,
** with the bank's register value in the third byte - and returns how many bytes are left in that
** bank, at most the 256 far_low_buf holds.
** Dividing by tedx_pages (189 on a Hannes board) and building a long in C take a good part of a
** frame on a Plus/4, so the bank last located is kept - the next copy is nearly always in the
** same one - and far_hw_src is put together a byte at a time. */
static unsigned tedx_first;                 /* the far pages of the bank last located... */
static unsigned tedx_end;                   /* ...up to here (0 to 0 until the first) */
static unsigned char tedx_bank;             /* ...and its number */

static unsigned tedx_locate (unsigned long a)
{
    unsigned page = *(unsigned*) ((unsigned char*) &a + 1);    /* a >> 8, without a long shift */
    unsigned char inbank;
    unsigned left;

    if (page < tedx_first || page >= tedx_end) {
        tedx_bank = page / tedx_pages;
        tedx_first = tedx_bank * tedx_pages;
        tedx_end = tedx_first + tedx_pages;
    }
    inbank = (unsigned char) (page - tedx_first);
    left = ((unsigned) (tedx_pages - inbank) << 8) - (unsigned char) a;
    ((unsigned char*) &far_hw_src)[0] = (unsigned char) a;
    ((unsigned char*) &far_hw_src)[1] = 0x40 + inbank;
    ((unsigned char*) &far_hw_src)[2] = tedx_select[tedx_bank];
    ((unsigned char*) &far_hw_src)[3] = 0;
    return left < 256 ? left : 256;
}
#endif

/* Copies len bytes from far memory at src into ordinary memory at dst, with whichever backend
** far_init chose - in several pieces where the hardware can't cross a bank in one go. */
static void hw_read (void* dst, farptr src, unsigned len)
{
#if HAS_REU || HAS_SCPU || HAS_CBM || HAS_TEDX
    unsigned n;
#endif
    unsigned char* d = dst;

    switch (backend) {
    case FAR_NEAR:
        memcpy (d, near_base + (unsigned) src, len);
        break;
#if HAS_EMD
    case FAR_EMD:
        /* cc65's driver API addresses its memory as 256-byte pages plus an offset. */
        request.buf = d;
        request.offs = (unsigned char) src;
        request.page = (unsigned) (src >> 8);
        request.count = len;
        em_copyfrom (&request);
        break;
#endif
#if HAS_REU
    case FAR_REU:
        while (len) {
            n = to_bank_end (src, len);
            far_hw_near = d;
            far_hw_src = src;
            far_hw_len = n;
            run_copy_hook ();
            reu_go (REU_FETCH);
            d += n;
            src += n;
            len -= n;
        }
        break;
#endif
#if HAS_SCPU
    case FAR_SCPU:
        /* Far offsets become 24-bit 65816 addresses; ordinary memory is bank 0. */
        src += scpu_base;
        while (len) {
            n = to_bank_end (src, len);
            far_hw_src = src;
            far_hw_dst = (unsigned) d;
            far_hw_len = n;
            scpu_move (0);
            d += n;
            src += n;
            len -= n;
        }
        break;
#endif
#if HAS_CBM
    case FAR_CBMBANK:
        while (len) {
            n = cbm_locate (src);
            if (n > len) {
                n = len;
            }
            far_hw_near = d;
            far_hw_len = n;
            run_copy_hook ();
            cbm_fetch ();
            d += n;
            src += n;
            len -= n;
        }
        break;
#endif
#if HAS_TEDX
    case FAR_RAMEXP:
        /* tedx_copy goes through far_low_buf, as wherever d is may be remapped while the bank
        ** is in. */
        while (len) {
            n = tedx_locate (src);
            if (n > len) {
                n = len;
            }
            far_hw_near = d;
            far_hw_len = n;
            run_copy_hook ();
            tedx_copy (0);
            d += n;
            src += n;
            len -= n;
        }
        break;
#endif
    }
}

/* The reverse of hw_read: copies len bytes from ordinary memory at src into far memory at dst. */
static void hw_write (farptr dst, const void* src, unsigned len)
{
#if HAS_REU || HAS_SCPU || HAS_CBM || HAS_TEDX
    unsigned n;
#endif
    const unsigned char* s = src;

    switch (backend) {
    case FAR_NEAR:
        memcpy (near_base + (unsigned) dst, s, len);
        break;
#if HAS_EMD
    case FAR_EMD:
        request.buf = (void*) s;
        request.offs = (unsigned char) dst;
        request.page = (unsigned) (dst >> 8);
        request.count = len;
        em_copyto (&request);
        break;
#endif
#if HAS_REU
    case FAR_REU:
        while (len) {
            n = to_bank_end (dst, len);
            far_hw_near = (void*) s;
            far_hw_src = dst;
            far_hw_len = n;
            run_copy_hook ();
            reu_go (REU_STASH);
            s += n;
            dst += n;
            len -= n;
        }
        break;
#endif
#if HAS_SCPU
    case FAR_SCPU:
        dst += scpu_base;
        while (len) {
            n = to_bank_end (dst, len);
            far_hw_src = (unsigned) s;
            far_hw_dst = dst;
            far_hw_len = n;
            scpu_move (0);
            s += n;
            dst += n;
            len -= n;
        }
        break;
#endif
#if HAS_CBM
    case FAR_CBMBANK:
        while (len) {
            n = cbm_locate (dst);
            if (n > len) {
                n = len;
            }
            far_hw_near = (void*) s;
            far_hw_len = n;
            run_copy_hook ();
            cbm_stash ();
            s += n;
            dst += n;
            len -= n;
        }
        break;
#endif
#if HAS_TEDX
    case FAR_RAMEXP:
        while (len) {
            n = tedx_locate (dst);
            if (n > len) {
                n = len;
            }
            far_hw_near = (void*) s;
            far_hw_len = n;
            run_copy_hook ();
            tedx_copy (1);
            s += n;
            dst += n;
            len -= n;
        }
        break;
#endif
    }
}

#if HAS_SCPU
/* SuperCPU far-to-far: MVN forward, or MVP from the top when the ranges overlap that way. Each
** move stops at whichever range reaches a bank boundary first. */
static void scpu_copy (unsigned long dst, unsigned long src, unsigned long len, unsigned char backward)
{
    unsigned n;
    unsigned room;

    dst += scpu_base;
    src += scpu_base;
    if (backward) {
        dst += len - 1;
        src += len - 1;
    }
    while (len) {
        n = len > 0xFFFFUL ? 0xFFFF : (unsigned) len;
        if (backward) {
            /* Room below, down to the start of each bank. */
            room = (unsigned) src + 1;
            if (room != 0 && room < n) n = room;
            room = (unsigned) dst + 1;
            if (room != 0 && room < n) n = room;
        } else {
            n = to_bank_end (src, n);
            n = to_bank_end (dst, n);
        }
        far_hw_src = src;
        far_hw_dst = dst;
        far_hw_len = n;
        scpu_move (backward);
        if (backward) {
            src -= n;
            dst -= n;
        } else {
            src += n;
            dst += n;
        }
        len -= n;
    }
}
#endif

/* Copies len bytes within far memory, like memmove. */
static void hw_copy (farptr dst, farptr src, unsigned long len)
{
    unsigned n;
    /* Copying upwards over an overlap would overwrite the source before it's read, so that case
    ** is done from the top down. */
    unsigned char backward = dst > src && dst < src + len;

    if (backend == FAR_NEAR) {
        memmove (near_base + (unsigned) dst, near_base + (unsigned) src, (unsigned) len);
        return;
    }
#if HAS_SCPU
    if (backend == FAR_SCPU) {
        scpu_copy (dst, src, len, backward);
        return;
    }
#endif
    /* Everything else bounces through ordinary memory, from the top when the ranges overlap so
    ** the source isn't overwritten before it's read. */
    if (backward) {
        src += len;
        dst += len;
    }
    while (len) {
        n = len > BOUNCE_SIZE ? BOUNCE_SIZE : (unsigned) len;
        if (backward) {
            src -= n;
            dst -= n;
        }
        hw_read (bounce, src, n);
        hw_write (dst, bounce, n);
        if (!backward) {
            src += n;
            dst += n;
        }
        len -= n;
    }
}

/* Sets len bytes of far memory at dst to value, like memset. */
static void hw_fill (farptr dst, unsigned char value, unsigned long len)
{
    unsigned n;

    if (backend == FAR_NEAR) {
        memset (near_base + (unsigned) dst, value, (unsigned) len);
        return;
    }
#if HAS_REU
    if (backend == FAR_REU) {
        /* One DMA per 64K: the REU reads the same C64 byte over and over. */
        bounce[0] = value;
        while (len) {
            n = to_bank_end (dst, len > 0xFFFFUL ? 0xFFFF : (unsigned) len);
            far_hw_near = bounce;
            far_hw_src = dst;
            far_hw_len = n;
            far_hw_ctrl = 0x80;     /* address control: hold the C64 address still */
            run_copy_hook ();
            reu_go (REU_STASH);
            far_hw_ctrl = 0;
            dst += n;
            len -= n;
        }
        return;
    }
#endif
#if HAS_SCPU
    if (backend == FAR_SCPU) {
        /* One byte, then MVN copies each byte onto the next. */
        hw_write (dst, &value, 1);
        if (len > 1) {
            scpu_copy (dst + 1, dst, len - 1, 0);
        }
        return;
    }
#endif
    /* Everything else: a buffer full of the value, written out as many times as it takes. */
    memset (bounce, value, BOUNCE_SIZE);
    while (len) {
        n = len > BOUNCE_SIZE ? BOUNCE_SIZE : (unsigned) len;
        hw_write (dst, bounce, n);
        dst += n;
        len -= n;
    }
}

/* ------------------------------------------------------------------------
** The window cache: FAR_WINDOWS buffers of ordinary memory, each a copy of a range of far
** memory, replaced least recently used first. Every other access writes back (and, if it
** changes far memory, drops) the windows it overlaps, so both views stay the same.
*/

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
        hw_write (windows[i].base, window_data[i], windows[i].len);
        windows[i].dirty = 0;
    }
}

/* Writes back any changed window overlapping [a, a + len), and drops them too if drop is set.
** Without drop, an unchanged window needs nothing, so it's passed over before the comparisons of
** longs, which are slow on a 6502. */
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

    /* The range must fit in one window and lie inside far memory. */
    if (len == 0 || len > FAR_WINDOW_SIZE || p + len > size) {
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
        len = (size - p) < FAR_WINDOW_SIZE ? (unsigned) (size - p) : FAR_WINDOW_SIZE;
        /* No two windows may hold the same byte, or one's changes would hide the other's. */
        sync_range (p, len, 1);
        hw_read (window_data[victim], p, len);
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

/* The single-byte and word accessors below map just the bytes they need; a bad address reads
** as 0 and writes nowhere. */

void far_commit (void)
{
    unsigned char i;
    for (i = 0; i < FAR_WINDOWS; ++i) {
        write_back (i);
    }
}

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

/* ------------------------------------------------------------------------
** Block transfers. Each first brings the window cache into line: a read needs any changed window
** it overlaps written back first; a write also drops those windows, as they'd now be stale.
*/

void far_read (void* dst, farptr src, unsigned len)
{
    if (len) {
        sync_range (src, len, 0);
        hw_read (dst, src, len);
    }
}

void far_write (farptr dst, const void* src, unsigned len)
{
    if (len) {
        sync_range (dst, len, 1);
        hw_write (dst, src, len);
    }
}

void far_copy (farptr dst, farptr src, unsigned long len)
{
    if (len && dst != src) {
        sync_range (src, len, 0);
        sync_range (dst, len, 1);
        hw_copy (dst, src, len);
    }
}

void far_fill (farptr dst, unsigned char value, unsigned long len)
{
    if (len) {
        sync_range (dst, len, 1);
        hw_fill (dst, value, len);
    }
}

/* ------------------------------------------------------------------------
** The allocator. Far memory is a chain of blocks, each starting with a 4-byte header: the block's
** whole size (a multiple of 4), with bit 0 set while it's in use. Free neighbours are merged as a
** search passes them, and a search starts where the last one ended ("next fit"), so repeated
** allocations don't rescan the full blocks at the bottom.
*/

#define HEADER 4UL
#define USED   1UL

static unsigned long heap_end;              /* the end of the last block */
static farptr rover;                        /* where the next search starts */

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

    for (next = block + bsize; next < heap_end; next += h) {
        h = header_at (next);
        if (h & USED) {
            break;
        }
        /* The rover can't be left pointing into the middle of the merged block. */
        if (next == rover) {
            rover = block;
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

    if (n == 0 || n > size) {
        return FAR_NULL;
    }
    /* The block's whole size: the request plus its header, rounded up to a multiple of 4 so
    ** bit 0 of every size is free for the in-use flag. */
    need = (n + HEADER + 3) & ~3UL;

    /* Walk the chain from the rover to the end, then from 0 back up to the rover. */
    start = rover;
    block = rover;
    for (;;) {
        if (block >= heap_end) {
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
                rover = block;
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
    if (p < HEADER || p >= heap_end) {
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

    for (block = 0; block < heap_end; block += h & ~USED) {
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

/* ------------------------------------------------------------------------
** Setup.
*/

#if HAS_REU || HAS_EMD || HAS_SCPU
/* Installs a statically linked cc65 driver; its page count, or 0 (and uninstalled) if the
** hardware isn't there. */
static unsigned try_driver (void* driver)
{
    unsigned pages;
    if (em_install (driver) != EM_ERR_OK) {
        return 0;
    }
    pages = em_pagecount ();
    if (pages == 0) {
        em_uninstall ();
    }
    return pages;
}
#endif

#if HAS_CBM
/* Counts the RAM banks from CBM_FIRST_BANK up, by writing a different byte to each and checking
** every one still reads back - a missing bank doesn't keep what's written, and one that mirrors
** another changes it. */
static unsigned char cbm_count_banks (void)
{
    unsigned char marks[14];
    unsigned char count;
    unsigned char i;
    unsigned char b;

    far_hw_len = 1;
    /* Bank 15 is the system bank (I/O and ROMs), so banks up to 14 are tried. */
    for (count = 0; CBM_FIRST_BANK + count < 15 && count < sizeof marks; ++count) {
        /* Mark $0100 of the next bank, then check that bank and every one below it. */
        far_hw_src = ((unsigned long) (CBM_FIRST_BANK + count) << 16) | 0x0100;
        marks[count] = 0xA5 ^ count;
        far_hw_near = &marks[count];
        cbm_stash ();
        for (i = 0; i <= count; ++i) {
            far_hw_src = ((unsigned long) (CBM_FIRST_BANK + i) << 16) | 0x0100;
            far_hw_near = &b;
            b = ~marks[i];      /* so a fetch that changes nothing can't pass */
            cbm_fetch ();
            if (b != marks[i]) {
                return count;
            }
        }
    }
    return count;
}
#endif

#if HAS_TEDX
/* Looks for a Hannes board, then a Csory one, and sets up tedx_select, the register and the
** pages per bank for whichever is there. Returns the pages found, or 0 if neither is. */
static unsigned tedx_detect (void)
{
    unsigned char i;
    unsigned char bank;
    unsigned char banks;

    /* Hannes ($FD16): bits 0-1 pick the bank and, on the 1MB board, the inverse of bits 2-3 its
    ** top two bits. Bank 3 is the machine's own RAM, so the others are 0-2, then 4-15. Bit 7
    ** keeps $1000-$3FFF the machine's own, so only $4000 up switches; below the I/O at $FD00,
    ** that's 189 pages a bank. On the 256K board, banks past 2 repeat 0-2, which tedx_count
    ** sees. */
    for (i = 0; i < 15; ++i) {
        bank = i < 3 ? i : i + 1;
        tedx_select[i] = 0x80 | (bank & 3) | ((3 - (bank >> 2)) << 2);
    }
    far_hw_dst = 0xFD16;
    far_hw_ctrl = 0x8F;                     /* bank 3 */
    banks = tedx_count (15);
    if (banks) {
        tedx_pages = 189;
        tedx_name = banks > 3 ? "Hannes 1MB" : "Hannes 256K";
        return banks * 189;
    }

    /* Csory ($FD15): bits 6-7 pick which 16K of the address space to remap, bits 0-3 which of
    ** the board's 16 blocks to put there. It starts with block 15 at $C000, standing in for the
    ** machine's own RAM there, so blocks 0-14 are put at $4000 one at a time. */
    for (i = 0; i < 15; ++i) {
        tedx_select[i] = 0x40 | i;
    }
    far_hw_dst = 0xFD15;
    far_hw_ctrl = 0xCF;                     /* block 15 at $C000 */
    banks = tedx_count (15);
    if (banks) {
        tedx_pages = 64;
        tedx_name = "Csory 256K";
        return banks * 64;
    }
    return 0;
}
#endif

unsigned char far_init (void)
{
    unsigned pages = 0;
    unsigned avail;

    /* Already set up: nothing to do. */
    if (backend != FAR_NONE) {
        return backend;
    }
    memset (windows, 0, sizeof windows);
    far_hw_ctrl = 0;

    /* Try each kind of expansion this machine can have, fastest first. */
#if defined(__C64__)
    if ((pages = try_driver (c64_65816_emd)) != 0) {
        backend = FAR_SCPU;
        /* The driver's page 0 is the first bank the SuperCPU leaves free: bank 2 (bank 1 holds
        ** its ROM copies), or bank 1 on other 65816 hardware. */
        scpu_base = (*(unsigned char*) 0xD0BC & 0x80) ? 0x10000UL : 0x20000UL;
    } else if ((pages = try_driver (c64_reu_emd)) != 0) {
        backend = FAR_REU;
    }
#elif defined(__C128__)
    if ((pages = try_driver (c128_reu_emd)) != 0) {
        backend = FAR_REU;
    } else if ((pages = try_driver (c128_ram2_emd)) != 0) {
        backend = FAR_EMD;
    }
#elif HAS_TEDX
    if ((pages = tedx_detect ()) != 0) {
        backend = FAR_RAMEXP;
    }
#  if HAS_EMD
    else if ((pages = try_driver (c16_ram_emd)) != 0) {
        backend = FAR_EMD;
    }
#  endif
#elif HAS_CBM
    cbm_banks = cbm_count_banks ();
    if (cbm_banks) {
        pages = cbm_banks * 255;
        backend = FAR_CBMBANK;
    }
#endif

    if (backend != FAR_NONE) {
        size = (unsigned long) pages << 8;      /* 256 bytes per page */
    } else {
        /* No expansion: half of what's left of the C heap stands in, so the program still runs. */
        avail = _heapmaxavail () / 2;
        avail &= ~3U;
        if (avail < 64 || (near_base = malloc (avail)) == 0) {
            return FAR_NONE;
        }
        size = avail;
        backend = FAR_NEAR;
    }

    /* One free block covering everything. */
    heap_end = size & ~3UL;
    rover = 0;
    set_header (0, heap_end);
    return backend;
}

void far_done (void)
{
    if (backend == FAR_NONE) {
        return;
    }
    /* Save any changed windows, release whatever far_init took, and reset everything. */
    far_commit ();
    if (backend == FAR_NEAR) {
        free (near_base);
    }
#if HAS_REU || HAS_EMD || HAS_SCPU
    /* The backends that came from a cc65 driver. */
    else if (backend == FAR_REU || backend == FAR_EMD || backend == FAR_SCPU) {
        em_uninstall ();
    }
#endif
    memset (windows, 0, sizeof windows);
    backend = FAR_NONE;
    size = 0;
}

unsigned char far_backend (void)
{
    return backend;
}

const char* far_backend_name (void)
{
    /* Only the backends this target can have are listed. */
    switch (backend) {
    case FAR_NEAR:    return "ordinary RAM";
#if defined(__C64__) || defined(__C128__)
    case FAR_REU:     return "REU";
#endif
#if defined(__C64__)
    case FAR_SCPU:    return "SuperCPU RAM";
#endif
#if defined(__C128__)
    case FAR_EMD:     return "C128 RAM banks";
#elif HAS_EMD
    case FAR_EMD:     return "RAM under ROM";
#endif
#if HAS_CBM
    case FAR_CBMBANK: return "RAM banks";
#endif
#if HAS_TEDX
    case FAR_RAMEXP:  return tedx_name;
#endif
    }
    return "none";
}

unsigned long far_size (void)
{
    return size;
}
