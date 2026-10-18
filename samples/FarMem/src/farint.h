/*
** farint.h - what FarMem's own modules share; not for programs, which use farmem.h.
**
** FarMem is split into modules so that a program links only what it uses - ld65 takes whole
** object files from a library, and only those something refers to:
**   farmem.c    the core: setup, the backends, far_read and far_write
**   farwin.c    the window cache: far_map, far_commit, far_peek/poke(w)
**   farcopy.c   far_copy and far_fill
**   faralloc.c  the allocator: far_alloc, far_free, far_maxavail
** The core doesn't call the window cache directly: far_map sets fm_sync, which the core's
** transfers call to keep the windows in line, so a program that never maps a window doesn't
** link the cache.
*/

#ifndef FARINT_H
#define FARINT_H

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

/* farmem.c's state. */
extern unsigned char fm_backend;            /* a FAR_ value, chosen by far_init */
extern unsigned long fm_size;               /* bytes of far memory */
extern unsigned char* fm_near_base;         /* FAR_NEAR's block */
#if HAS_SCPU
extern unsigned long fm_scpu_base;          /* far offset 0 as a 65816 address */
#endif
extern unsigned long fm_heap_end;           /* the allocator's: the end of the last block... */
extern farptr fm_rover;                     /* ...and where its next search starts */

/* The window cache's hook: writes back any changed window overlapping [a, a + len), and drops
** them too if drop is set. 0 until far_map first runs. */
extern void (*fm_sync) (farptr a, unsigned long len, unsigned char drop);

/* Calls the program's copy hook (far_set_copy_hook), if it has one. */
void fm_run_copy_hook (void);

#if HAS_REU || HAS_SCPU
/* How much of len fits before a crosses a 64K boundary. */
unsigned fm_to_bank_end (unsigned long a, unsigned len);
#endif

/* Copies len (not 0) bytes between ordinary memory and far memory with the chosen backend,
** without the window cache. */
void fm_read (void* dst, farptr src, unsigned len);
void fm_write (farptr dst, const void* src, unsigned len);

#endif
