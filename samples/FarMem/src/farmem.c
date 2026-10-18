/*
** farmem.c - far memory's core: backend selection, and transfers between ordinary and far memory,
** split to what each backend can do in one go. farint.h lists the other modules.
*/

#include <stdlib.h>
#include <string.h>
#include <em.h>
#include "farint.h"

/* The program's copy hook (far_set_copy_hook), called before each copy with interrupts off. */
static void (*copy_hook) (void);

void fm_run_copy_hook (void)
{
    if (copy_hook) {
        copy_hook ();
    }
}

void far_set_copy_hook (void (*hook) (void))
{
    copy_hook = hook;
}

unsigned char fm_backend;
unsigned long fm_size;
unsigned char* fm_near_base;
#if HAS_SCPU
unsigned long fm_scpu_base;
#endif
unsigned long fm_heap_end;
farptr fm_rover;
void (*fm_sync) (farptr a, unsigned long len, unsigned char drop);
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
unsigned fm_to_bank_end (unsigned long a, unsigned len)
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
void fm_read (void* dst, farptr src, unsigned len)
{
#if HAS_REU || HAS_SCPU || HAS_CBM || HAS_TEDX
    unsigned n;
#endif
    unsigned char* d = dst;

    switch (fm_backend) {
    case FAR_NEAR:
        memcpy (d, fm_near_base + (unsigned) src, len);
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
            n = fm_to_bank_end (src, len);
            far_hw_near = d;
            far_hw_src = src;
            far_hw_len = n;
            fm_run_copy_hook ();
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
        src += fm_scpu_base;
        while (len) {
            n = fm_to_bank_end (src, len);
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
            fm_run_copy_hook ();
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
            fm_run_copy_hook ();
            tedx_copy (0);
            d += n;
            src += n;
            len -= n;
        }
        break;
#endif
    }
}

/* The reverse of fm_read: copies len bytes from ordinary memory at src into far memory at dst. */
void fm_write (farptr dst, const void* src, unsigned len)
{
#if HAS_REU || HAS_SCPU || HAS_CBM || HAS_TEDX
    unsigned n;
#endif
    const unsigned char* s = src;

    switch (fm_backend) {
    case FAR_NEAR:
        memcpy (fm_near_base + (unsigned) dst, s, len);
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
            n = fm_to_bank_end (dst, len);
            far_hw_near = (void*) s;
            far_hw_src = dst;
            far_hw_len = n;
            fm_run_copy_hook ();
            reu_go (REU_STASH);
            s += n;
            dst += n;
            len -= n;
        }
        break;
#endif
#if HAS_SCPU
    case FAR_SCPU:
        dst += fm_scpu_base;
        while (len) {
            n = fm_to_bank_end (dst, len);
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
            fm_run_copy_hook ();
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
            fm_run_copy_hook ();
            tedx_copy (1);
            s += n;
            dst += n;
            len -= n;
        }
        break;
#endif
    }
}

/* ------------------------------------------------------------------------
** Transfers. Each first brings the window cache (farwin.c), if the program uses it, into line: a
** read needs any changed window it overlaps written back first; a write also drops those
** windows, as they'd now be stale.
*/

void far_read (void* dst, farptr src, unsigned len)
{
    if (len) {
        if (fm_sync) {
            fm_sync (src, len, 0);
        }
        fm_read (dst, src, len);
    }
}

void far_write (farptr dst, const void* src, unsigned len)
{
    if (len) {
        if (fm_sync) {
            fm_sync (dst, len, 1);
        }
        fm_write (dst, src, len);
    }
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
    unsigned long header;

    /* Already set up: nothing to do. */
    if (fm_backend != FAR_NONE) {
        return fm_backend;
    }
    far_hw_ctrl = 0;

    /* Try each kind of expansion this machine can have, fastest first. */
#if defined(__C64__)
    if ((pages = try_driver (c64_65816_emd)) != 0) {
        fm_backend = FAR_SCPU;
        /* The driver's page 0 is the first bank the SuperCPU leaves free: bank 2 (bank 1 holds
        ** its ROM copies), or bank 1 on other 65816 hardware. */
        fm_scpu_base = (*(unsigned char*) 0xD0BC & 0x80) ? 0x10000UL : 0x20000UL;
    } else if ((pages = try_driver (c64_reu_emd)) != 0) {
        fm_backend = FAR_REU;
    }
#elif defined(__C128__)
    if ((pages = try_driver (c128_reu_emd)) != 0) {
        fm_backend = FAR_REU;
    } else if ((pages = try_driver (c128_ram2_emd)) != 0) {
        fm_backend = FAR_EMD;
    }
#elif HAS_TEDX
    if ((pages = tedx_detect ()) != 0) {
        fm_backend = FAR_RAMEXP;
    }
#  if HAS_EMD
    else if ((pages = try_driver (c16_ram_emd)) != 0) {
        fm_backend = FAR_EMD;
    }
#  endif
#elif HAS_CBM
    cbm_banks = cbm_count_banks ();
    if (cbm_banks) {
        pages = cbm_banks * 255;
        fm_backend = FAR_CBMBANK;
    }
#endif

    if (fm_backend != FAR_NONE) {
        fm_size = (unsigned long) pages << 8;      /* 256 bytes per page */
    } else {
        /* No expansion: half of what's left of the C heap stands in, so the program still runs. */
        avail = _heapmaxavail () / 2;
        avail &= ~3U;
        if (avail < 64 || (fm_near_base = malloc (avail)) == 0) {
            return FAR_NONE;
        }
        fm_size = avail;
        fm_backend = FAR_NEAR;
    }

    /* One free block covering everything, for the allocator (faralloc.c). */
    fm_heap_end = header = fm_size & ~3UL;
    fm_rover = 0;
    fm_write (0, &header, sizeof header);
    return fm_backend;
}

void far_done (void)
{
    if (fm_backend == FAR_NONE) {
        return;
    }
    /* Save any changed windows and drop them all, release whatever far_init took, and reset
    ** everything. */
    if (fm_sync) {
        fm_sync (0, fm_size, 1);
    }
    if (fm_backend == FAR_NEAR) {
        free (fm_near_base);
    }
#if HAS_REU || HAS_EMD || HAS_SCPU
    /* The backends that came from a cc65 driver. */
    else if (fm_backend == FAR_REU || fm_backend == FAR_EMD || fm_backend == FAR_SCPU) {
        em_uninstall ();
    }
#endif
    fm_backend = FAR_NONE;
    fm_size = 0;
}

unsigned char far_backend (void)
{
    return fm_backend;
}

const char* far_backend_name (void)
{
    /* Only the backends this target can have are listed. */
    switch (fm_backend) {
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
    return fm_size;
}
