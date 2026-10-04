/*
** fartest.c - exercises farmem on whatever this machine has, and shows each check's result.
** Run in VICE with -debugcart, it also exits the emulator with 0 when everything passed, or
** $80 + the first failing check's number, so scripts/Test-FarMem.ps1 can run every machine.
*/

#include <conio.h>
#include <string.h>
#include <stdlib.h>
#include "farmem.h"

#if defined(__CBM510__)
#  include <cbm510.h>
#elif defined(__CBM610__)
#  include <cbm610.h>
#endif

#define BUF 600

static unsigned char buf[BUF];
static unsigned char buf2[BUF];
static unsigned char check_no;
static unsigned char first_failure;
static char number[12];

static void put_number (unsigned long v)
{
    cputs (ultoa (v, number, 10));
}

static void check (unsigned char ok, const char* what)
{
    ++check_no;
    cputs (ok ? "ok   " : "FAIL ");
    cputs (what);
    cputs ("\r\n");
    if (!ok && !first_failure) {
        first_failure = check_no;
    }
}

static void skip (const char* what)
{
    ++check_no;
    cputs ("skip ");
    cputs (what);
    cputs ("\r\n");
}

/* VICE's debug cartridge: a write here ends the emulator, with the value as its exit code. On real
** hardware the address is unused. */
static void report (unsigned char code)
{
#if defined(__CBM510__) || defined(__CBM610__)
    pokebsys (0xDAFF, code);
#elif defined(__C16__)
    *(volatile unsigned char*) 0xFDCF = code;
#else
    *(volatile unsigned char*) 0xD7FF = code;
#endif
}

static void pattern (unsigned char* b, unsigned len, unsigned char seed)
{
    unsigned i;
    for (i = 0; i < len; ++i) {
        b[i] = (unsigned char) (i * 7 + seed);
    }
}

static unsigned char all (const unsigned char* b, unsigned len, unsigned char value)
{
    unsigned i;
    for (i = 0; i < len; ++i) {
        if (b[i] != value) {
            return 0;
        }
    }
    return 1;
}

/* Blocks spanning a 64K boundary, on backends big enough for one. */
static void test_large (void)
{
    farptr a;
    farptr b;
    unsigned long i;
    unsigned char ok = 1;

    if (far_size () < 150000UL) {
        skip ("over 64K (needs 150000 bytes)");
        skip ("64K copy");
        return;
    }
    a = far_alloc (70000UL);
    b = far_alloc (70000UL);
    check (a != FAR_NULL && b != FAR_NULL, "two 70000-byte blocks");
    if (a == FAR_NULL || b == FAR_NULL) {
        return;
    }

    /* A pattern written in pieces, read back across the 64K boundary. */
    for (i = 0; i + BUF <= 70000UL; i += BUF) {
        pattern (buf, BUF, (unsigned char) (i >> 8));
        far_write (a + i, buf, BUF);
    }
    for (i = 0; i + BUF <= 70000UL && ok; i += BUF) {
        pattern (buf2, BUF, (unsigned char) (i >> 8));
        far_read (buf, a + i, BUF);
        ok = memcmp (buf, buf2, BUF) == 0;
    }
    check (ok, "70000 bytes written and read");

    far_fill (b, 0xEE, 70000UL);
    far_copy (b + 1000, a, 68000UL);
    /* a + 65000 is 200 bytes into the piece written at a + 64800. */
    far_read (buf, b + 1000 + 65000UL, BUF - 200);
    pattern (buf2, BUF, (unsigned char) (64800UL >> 8));
    ok = memcmp (buf, buf2 + 200, BUF - 200) == 0;
    far_read (buf, b, 1000);
    ok = ok && all (buf, 1000, 0xEE) && far_peek (b + 69999UL) == 0xEE;
    check (ok, "68000-byte copy and fill");

    far_free (a);
    far_free (b);
}

int main (void)
{
    unsigned char backend;
    farptr a;
    farptr blocks[5];
    unsigned i;
    unsigned char ok;
    unsigned char* w;
    unsigned long before;

    clrscr ();
    cputs ("farmem test\r\n");
    backend = far_init ();
    cputs ("backend: ");
    cputs (far_backend_name ());
    cputs (", ");
    put_number (far_size ());
    cputs (" bytes\r\n\r\n");
    check (backend != FAR_NONE && far_size () > 2000, "init");
    if (backend == FAR_NONE) {
        report (0x80 | first_failure);
        return 1;
    }
#ifdef FAR_EXPECTED
    /* Each test project names the backend its VICE setup should give (Preprocessor defines). */
    check (backend == FAR_EXPECTED, "the expected backend");
#endif
    before = far_maxavail ();

    /* 1. Reading and writing across page boundaries. */
    a = far_alloc (1000);
    check (a != FAR_NULL, "alloc 1000");
    pattern (buf, BUF, 3);
    far_write (a + 200, buf, BUF);
    memset (buf2, 0, BUF);
    far_read (buf2, a + 200, BUF);
    check (memcmp (buf, buf2, BUF) == 0, "write and read 600");

    /* 2. Fill. */
    far_fill (a, 0x5A, 1000);
    far_read (buf2, a + 300, BUF);
    check (all (buf2, BUF, 0x5A) && far_peek (a + 999) == 0x5A, "fill 1000");

    /* 3. Overlapping copies, both ways. */
    for (i = 0; i < BUF; ++i) {
        buf[i] = (unsigned char) i;
    }
    far_write (a, buf, 500);
    far_copy (a + 100, a, 400);
    far_read (buf2, a + 100, 400);
    check (memcmp (buf, buf2, 400) == 0, "copy up (overlapping)");
    far_copy (a, a + 100, 400);
    far_read (buf2, a, 400);
    check (memcmp (buf, buf2, 400) == 0, "copy down (overlapping)");

    /* 4. Bytes and words through the window cache, seen by block reads. */
    for (i = 0; i < 1000; i += 7) {
        far_poke (a + i, (unsigned char) (i * 3));
    }
    far_pokew (a + 998, 0xBEEF);
    far_commit ();
    ok = 1;
    for (i = 0; i < 994; i += 7) {
        far_read (buf2, a + i, 1);
        ok = ok && buf2[0] == (unsigned char) (i * 3) && far_peek (a + i) == (unsigned char) (i * 3);
    }
    check (ok && far_peekw (a + 998) == 0xBEEF, "peek and poke");

    /* 5. A window changed but not committed is still what a block read sees. */
    w = far_map (a + 10, 100, FAR_WRITE);
    memset (w, 0x77, 100);
    far_read (buf2, a + 10, 100);
    ok = all (buf2, 100, 0x77);
    far_write (a + 50, buf, 10);
    w = far_map (a + 10, 100, FAR_READ);
    check (ok && w[39] == 0x77 && w[40] == 0 && w[49] == 9, "mapped windows stay in step");
    far_free (a);

    /* 6. The allocator: blocks keep their contents, freed space is reused and merged. */
    ok = 1;
    for (i = 0; i < 5; ++i) {
        blocks[i] = far_alloc (300 + i * 50);
        ok = ok && blocks[i] != FAR_NULL;
        if (blocks[i] != FAR_NULL) {
            far_fill (blocks[i], (unsigned char) (i + 1), 300 + i * 50);
        }
    }
    check (ok, "alloc 5 blocks");
    far_free (blocks[1]);
    far_free (blocks[3]);
    blocks[1] = far_alloc (330);
    far_fill (blocks[1], 0x22, 330);
    ok = blocks[1] != FAR_NULL;
    for (i = 0; i < 5; i += 2) {
        far_read (buf2, blocks[i], 300 + i * 50);
        ok = ok && all (buf2, 300 + i * 50, (unsigned char) (i + 1));
    }
    check (ok, "blocks keep their contents");
    far_free (blocks[0]);
    far_free (blocks[1]);
    far_free (blocks[2]);
    far_free (blocks[4]);
    check (far_maxavail () == before, "freed blocks merge again");
    check (far_alloc (far_size () + 1) == FAR_NULL, "too big fails");
    a = far_alloc (before);
    check (a != FAR_NULL, "alloc everything");
    far_free (a);

    /* 7. Large blocks. */
    test_large ();

    cputs (first_failure ? "\r\nFAILED\r\n" : "\r\nALL PASSED\r\n");
    report (first_failure ? 0x80 | first_failure : 0);
    far_done ();
    cputs ("press a key\r\n");
    cgetc ();
    return first_failure != 0;
}
