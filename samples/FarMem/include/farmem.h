/*
** farmem.h - one flat, 24-bit "far" address space over whatever extra memory the machine has,
** in the spirit of DOS/4GW and DPMI: allocate blocks of it, read, write, copy and fill them, or
** map a piece into ordinary memory to work on with normal C pointers.
**
** cc65's pointers are 16 bits wide, so far memory is never addressed with them directly: a farptr
** is a byte offset into the far space, from 0 to far_size() - 1, and every access goes through
** these functions.
**
** Backends, picked by far_init():
**   C64            SuperCPU SuperRAM (65816 long moves), else an REU (DMA), else ordinary RAM
**   C128           an REU (DMA), else RAM banks 1-3 (cc65's c128-ram2 driver)
**   C16 / Plus/4   the 64K machine's RAM under the ROMs (cc65's c16-ram driver; build for c16)
**   CBM 510        RAM bank 1 (6509 indirect access)
**   CBM 610        RAM banks 2-4 (6509 indirect access)
** Where there is no extra memory, a block of ordinary RAM stands in, so programs still run.
*/

#ifndef FARMEM_H
#define FARMEM_H

/* A byte offset into far memory. Only the low 24 bits are used. */
typedef unsigned long farptr;

/* What far_alloc returns when it can't: no block ever starts at offset 0. */
#define FAR_NULL 0UL

/* The backends far_init can choose - see far_backend(). */
#define FAR_NONE     0  /* far_init hasn't run, or failed */
#define FAR_NEAR     1  /* no expansion found: a block of ordinary RAM */
#define FAR_REU      2  /* Commodore REU (or compatible), by DMA */
#define FAR_SCPU     3  /* SuperCPU SuperRAM, by 65816 block moves */
#define FAR_EMD      4  /* a cc65 extended memory driver (C128 banks, C16 hidden RAM) */
#define FAR_CBMBANK  5  /* CBM-II RAM banks, by 6509 indirect access */

/* far_map modes. */
#define FAR_READ   0    /* the window is only read */
#define FAR_WRITE  1    /* the window is changed: written back on eviction or far_commit */

/* The largest range one far_map call can cover, and how many maps stay valid at once. A program
** can define these before building the library (with -D) to trade RAM for fewer transfers. */
#ifndef FAR_WINDOW_SIZE
#define FAR_WINDOW_SIZE 256
#endif
#ifndef FAR_WINDOWS
#define FAR_WINDOWS 4
#endif

/* Finds the machine's extra memory and sets up the allocator. Returns the backend (FAR_NEAR when
** there's no expansion), or FAR_NONE when even the stand-in block couldn't be had. */
unsigned char far_init (void);

/* Writes back any changed windows and releases the driver. */
void far_done (void);

unsigned char far_backend (void);
const char* far_backend_name (void);

/* The size of far memory, and the largest block far_alloc could return now. */
unsigned long far_size (void);
unsigned long far_maxavail (void);

/* Allocates size bytes of far memory (FAR_NULL if there isn't a free block that big). */
farptr far_alloc (unsigned long size);
void far_free (farptr p);

/* Moves bytes between ordinary memory and far memory. */
void far_read (void* dst, farptr src, unsigned len);
void far_write (farptr dst, const void* src, unsigned len);

/* Copies within far memory - overlapping ranges are fine, as with memmove. */
void far_copy (farptr dst, farptr src, unsigned long len);
void far_fill (farptr dst, unsigned char value, unsigned long len);

/* Single bytes and words, through the window cache. */
unsigned char far_peek (farptr p);
void far_poke (farptr p, unsigned char value);
unsigned far_peekw (farptr p);
void far_pokew (farptr p, unsigned value);

/* Maps len bytes (at most FAR_WINDOW_SIZE) at p into ordinary memory and returns a pointer to
** them. The pointer stays valid until FAR_WINDOWS further maps, or far_commit. With FAR_WRITE,
** changes made through it reach far memory on eviction or far_commit. */
void* far_map (farptr p, unsigned len, unsigned char mode);
void far_commit (void);

/* Arrays of fixed-size records in far memory: the address of element i of an array at base. */
#define FAR_ELEMENT(base, type, i) ((base) + (unsigned long) (i) * sizeof (type))
#define far_get(dst, base, type, i) far_read ((dst), FAR_ELEMENT (base, type, i), sizeof (type))
#define far_put(base, type, i, src) far_write (FAR_ELEMENT (base, type, i), (src), sizeof (type))

#endif
