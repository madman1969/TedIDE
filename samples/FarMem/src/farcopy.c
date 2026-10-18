/*
** farcopy.c - copying and filling within far memory: far_copy and far_fill.
*/

#include <string.h>
#include "farint.h"

#define BOUNCE_SIZE 256

static unsigned char bounce[BOUNCE_SIZE];   /* far-to-far copies and fills */

#if HAS_SCPU
/* SuperCPU far-to-far: MVN forward, or MVP from the top when the ranges overlap that way. Each
** move stops at whichever range reaches a bank boundary first. */
static void scpu_copy (unsigned long dst, unsigned long src, unsigned long len, unsigned char backward)
{
    unsigned n;
    unsigned room;

    dst += fm_scpu_base;
    src += fm_scpu_base;
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
            n = fm_to_bank_end (src, n);
            n = fm_to_bank_end (dst, n);
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

    if (fm_backend == FAR_NEAR) {
        memmove (fm_near_base + (unsigned) dst, fm_near_base + (unsigned) src, (unsigned) len);
        return;
    }
#if HAS_SCPU
    if (fm_backend == FAR_SCPU) {
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
        fm_read (bounce, src, n);
        fm_write (dst, bounce, n);
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

    if (fm_backend == FAR_NEAR) {
        memset (fm_near_base + (unsigned) dst, value, (unsigned) len);
        return;
    }
#if HAS_REU
    if (fm_backend == FAR_REU) {
        /* One DMA per 64K: the REU reads the same C64 byte over and over. */
        bounce[0] = value;
        while (len) {
            n = fm_to_bank_end (dst, len > 0xFFFFUL ? 0xFFFF : (unsigned) len);
            far_hw_near = bounce;
            far_hw_src = dst;
            far_hw_len = n;
            far_hw_ctrl = 0x80;     /* address control: hold the C64 address still */
            fm_run_copy_hook ();
            reu_go (REU_STASH);
            far_hw_ctrl = 0;
            dst += n;
            len -= n;
        }
        return;
    }
#endif
#if HAS_SCPU
    if (fm_backend == FAR_SCPU) {
        /* One byte, then MVN copies each byte onto the next. */
        fm_write (dst, &value, 1);
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
        fm_write (dst, bounce, n);
        dst += n;
        len -= n;
    }
}

/* Each first brings the window cache into line: the source's changed windows written back, and
** the destination's dropped too, as they'd now be stale. */

void far_copy (farptr dst, farptr src, unsigned long len)
{
    if (len && dst != src) {
        if (fm_sync) {
            fm_sync (src, len, 0);
            fm_sync (dst, len, 1);
        }
        hw_copy (dst, src, len);
    }
}

void far_fill (farptr dst, unsigned char value, unsigned long len)
{
    if (len) {
        if (fm_sync) {
            fm_sync (dst, len, 1);
        }
        hw_fill (dst, value, len);
    }
}
