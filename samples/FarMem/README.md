# Using the FarMem library

FarMem gives a cc65 program one flat block of "far" memory, up to 16MB, spread over whatever extra
memory the machine has: an REU, SuperCPU RAM, the C128's extra RAM banks, the RAM under the C16
and Plus/4 ROMs, or the CBM-II's RAM banks. Your program allocates blocks of it, copies data in and
out, and can map small pieces into ordinary memory to work on with normal C pointers.

The whole API is in [include/farmem.h](include/farmem.h). [test/fartest.c](test/fartest.c) is a
complete working program that uses every function, and [../WorldMap](../WorldMap) uses FarMem to
hold a scrolling 128K world map.

- [Far pointers](#far-pointers)
- [Adding FarMem to a project](#adding-farmem-to-a-project)
- [Starting and stopping](#starting-and-stopping)
- [Allocating blocks](#allocating-blocks)
- [Reading and writing](#reading-and-writing)
- [Arrays of records](#arrays-of-records)
- [Copying and filling](#copying-and-filling)
- [Single bytes and words](#single-bytes-and-words)
- [Mapped windows](#mapped-windows)
- [Running in VICE](#running-in-vice)
- [Tuning and limits](#tuning-and-limits)

## Far pointers

cc65's pointers are 16 bits wide, so they can't point into far memory. Instead, a far address is a
`farptr`: an `unsigned long` holding a byte offset from the start of far memory, from 0 to
`far_size() - 1`. You never dereference a `farptr`. Every access goes through a FarMem function,
but you can do arithmetic on it like any other number:

```c
farptr record = table + 10 * 32;    /* 32-byte records: the address of record 10 */
```

`FAR_NULL` (0) means "no block". `far_alloc` returns it on failure, and never returns it for a real
block.

## Adding FarMem to a project

Each machine has its own library project in this solution, all built from the same `src/` and
`include/`:

| Your project's target | Library project | Far memory it finds |
| --- | --- | --- |
| C64 | `FarMemC64` | SuperCPU SuperRAM, else an REU, else ordinary RAM |
| C128 | `FarMemC128` | An REU, else RAM banks 1-3 (cc65's `c128-ram2` driver) |
| C16 (also for a 64K Plus/4) | `FarMemC16` | The RAM under the ROMs (cc65's `c16-ram` driver), about 32,000 bytes |
| CBM 510 | `FarMemCBM510` | RAM bank 1 |
| CBM 610 | `FarMemCBM610` | RAM banks 2-4 |

To use one from your own program in Tedide:

1. Right-click your solution and choose **Add Existing Project...**, then pick the library's
   `.tproj` (for example `FarMemC64/FarMemC64.tproj`). A reference must be in the same solution as
   your project.
2. Open your project's **Settings...** and tick the library on the **References** tab.
3. `#include "farmem.h"` in your source files.

The library then builds before your program, its `include/` folder is added to your include
paths, and its `.lib` is linked in. You don't need to add anything else.

Outside Tedide, add `src/farmem.c` and `src/farhw.s` to your `cl65` command line with
`-I include`.

**C16 and Plus/4:** build for the **C16** target, not Plus/4. Only the C16 build uses the RAM
under the ROMs, and a Plus/4 build falls back to ordinary RAM. The machine needs 64K. Your program
must also stay below `$8000`, because the driver uses the RAM above it. To do that, use
[FarTestPlus4/c16-32k.cfg](FarTestPlus4/c16-32k.cfg) as your project's custom linker config.

## Starting and stopping

Call `far_init()` once, before anything else. It finds the machine's extra memory, sets up the
allocator and returns the backend it chose. Call `far_done()` before your program exits. It saves
any changes still held in mapped windows and releases the memory driver.

```c
#include <stdio.h>
#include "farmem.h"

int main (void)
{
    if (far_init () == FAR_NONE) {
        puts ("Not enough memory to start FarMem");
        return 1;
    }

    printf ("Far memory: %lu bytes of %s\n", far_size (), far_backend_name ());

    /* ... use far memory ... */

    far_done ();
    return 0;
}
```

`far_backend()` returns the same value as `far_init()`:

| Value | Meaning |
| --- | --- |
| `FAR_NONE` | `far_init` hasn't run, or failed |
| `FAR_NEAR` | No expansion found: half of the free C heap stands in, so the program still runs |
| `FAR_REU` | A Commodore REU or compatible, by DMA |
| `FAR_SCPU` | SuperCPU SuperRAM, by 65816 block moves |
| `FAR_EMD` | A cc65 extended memory driver (C128 RAM banks, C16 RAM under the ROMs) |
| `FAR_CBMBANK` | CBM-II RAM banks, by 6509 indirect access |

If your program needs real extra memory, check for `FAR_NEAR` and say so, rather than running with
a few kilobytes.

## Allocating blocks

`far_alloc(size)` returns the far address of a new block of `size` bytes, or `FAR_NULL` if there
isn't a free block that big. `far_free(p)` gives it back. Freed blocks are merged with their free
neighbours, so space doesn't fragment away.

```c
farptr text = far_alloc (20000UL);      /* bigger than most programs' whole heap */

if (text == FAR_NULL) {
    puts ("Out of far memory");
    return;
}

/* ... */

far_free (text);
```

`far_maxavail()` returns the size of the largest block `far_alloc` could return right now:

```c
printf ("Largest free block: %lu bytes\n", far_maxavail ());
```

A new block's contents are whatever was there before. Use `far_fill` to clear it if you need to.

## Reading and writing

`far_write(dst, src, len)` copies `len` bytes from ordinary memory into far memory, and
`far_read(dst, src, len)` copies them back. They're the far versions of `memcpy`, and the fastest
way to move data in and out.

```c
static char line[80];
farptr lines = far_alloc (500UL * 80);

/* Store line 123 */
strcpy (line, "Hello from far memory");
far_write (lines + 123UL * 80, line, sizeof line);

/* ...and read it back later */
far_read (line, lines + 123UL * 80, sizeof line);
```

Use `UL` constants or casts when multiplying. `123 * 80` is an `int` calculation in cc65 and
overflows past 32,767.

## Arrays of records

Three macros handle arrays of fixed-size records:

- `FAR_ELEMENT(base, type, i)` is the far address of element `i`.
- `far_put(base, type, i, src)` writes element `i` from `*src`.
- `far_get(dst, base, type, i)` reads element `i` into `*dst`.

```c
typedef struct {
    char name[16];
    unsigned score;
} player;

farptr players = far_alloc (2000UL * sizeof (player));
player p;

strcpy (p.name, "ADA");
p.score = 950;
far_put (players, player, 1500, &p);

far_get (&p, players, player, 1500);
printf ("%s scored %u\n", p.name, p.score);
```

This is a convenient way to keep a table too big for ordinary memory, reading one record at a
time into a local variable.

## Copying and filling

`far_copy(dst, src, len)` copies within far memory, and works like `memmove`: the source and
destination may overlap. `far_fill(dst, value, len)` sets `len` bytes to `value`, like `memset`.
Both take an `unsigned long` length, so they can move more than 64K at once.

```c
farptr screens = far_alloc (8UL * 1000);   /* eight 40x25 screens */

far_fill (screens, ' ', 8UL * 1000);        /* all blank */
far_copy (screens + 1000, screens, 1000);   /* screen 1 = screen 0 */

/* Scroll all eight screens' contents along by one screen: overlapping is fine */
far_copy (screens + 1000, screens, 7UL * 1000);
```

On an REU a fill is a single DMA transfer, and on a SuperCPU copies are 65816 block moves. Large
fills and copies are much faster than doing the same thing yourself.

## Single bytes and words

`far_peek` and `far_poke` read and write one byte. `far_peekw` and `far_pokew` read and write a
16-bit word, low byte first, as the 6502 stores them.

```c
farptr counts = far_alloc (256);
unsigned char c;

far_fill (counts, 0, 256);

/* Count how often each byte value appears in a buffer */
for (i = 0; i < len; ++i) {
    c = buf[i];
    far_poke (counts + c, far_peek (counts + c) + 1);
}

far_pokew (header, 0xBEEF);
version = far_peekw (header + 2);
```

These go through the window cache (see the next section), so repeated access to nearby addresses
doesn't go back to the hardware each time. Each one is still a function call, though. For loops
over a range, map it once or use `far_read` and `far_write`.

## Mapped windows

`far_map(p, len, mode)` copies up to `FAR_WINDOW_SIZE` (256) bytes of far memory into a buffer in
ordinary memory and returns a normal C pointer to them. Use `FAR_READ` if you'll only read through
the pointer. Use `FAR_WRITE` if you'll change the data, so the changes are written back to far
memory later.

```c
unsigned char* row;
unsigned char x;

/* Invert one 40-byte row of a far bitmap in place */
row = far_map (bitmap + y * 40UL, 40, FAR_WRITE);
if (row) {
    for (x = 0; x < 40; ++x) {
        row[x] ^= 0xFF;
    }
}

far_commit ();      /* write every changed window back now */
```

`far_map` returns a null pointer if `len` is 0, more than `FAR_WINDOW_SIZE`, or runs past the end
of far memory.

FarMem keeps `FAR_WINDOWS` (4) windows. When all of them are in use, mapping a new range replaces
the one used longest ago, and writes it back first if it was mapped with `FAR_WRITE`. Follow these
rules:

- **A pointer stays valid until `FAR_WINDOWS` further maps.** `far_peek`, `far_poke`, `far_peekw`
  and `far_pokew` count as maps too. Using two or three windows at once is fine. Keeping a pointer
  for a long time isn't, so map the range again when you come back to it.
- **Map with `FAR_WRITE` before changing anything.** Changes made through a `FAR_READ` pointer may
  be thrown away.
- **Don't change bytes through a pointer after a `far_write`, `far_copy` or `far_fill` over the
  same range.** These functions discard the window, so map the range again.
- **Changes reach far memory when the window is replaced, at `far_commit()` or at `far_done()`.**
  `far_read`, `far_copy` and the other block functions always see the latest changes, so you only
  need `far_commit` before something outside FarMem looks at the memory.

Windows suit loops that work on one small structure or row at a time:

```c
typedef struct {
    int x, y;
    signed char dx, dy;
} ball;

ball* b;
unsigned i;

for (i = 0; i < 500; ++i) {
    b = far_map (FAR_ELEMENT (balls, ball, i), sizeof (ball), FAR_WRITE);
    b->x += b->dx;
    b->y += b->dy;
}
```

## Running in VICE

Give VICE the hardware your program expects in the project's **Extra VICE arguments**, on the
Emulator tab of Project Settings. The test projects use these:

| Machine | Settings |
| --- | --- |
| C64 or C128 with an REU | `-reu -reusize 512` |
| C64 with a SuperCPU | Tick **Enable SuperCPU support**, and add `-simmsize 1` |
| C16 with 64K | `-ramsize 64` |
| CBM 610 with 256K | `-ramsize 256` |

A plain C128 or CBM 510 needs nothing extra.

## Tuning and limits

- **Memory used:** FarMem takes `FAR_WINDOWS × FAR_WINDOW_SIZE` bytes for its windows (1K by
  default), plus a 256-byte buffer for far-to-far copies and fills.
- **Changing the window sizes:** define `FAR_WINDOW_SIZE` and `FAR_WINDOWS` in the library
  project's **Preprocessor defines** (for example `FAR_WINDOW_SIZE=512`), and in your own
  project's too, so both see the same `far_map` limit. Bigger windows mean fewer transfers;
  smaller ones save RAM.
- **Transfer sizes:** `far_read` and `far_write` move at most 65,535 bytes per call. `far_copy`
  and `far_fill` have no such limit.
- **No bounds checks:** `far_read`, `far_write`, `far_copy` and `far_fill` don't check that the
  addresses are inside a block you allocated. Stay within your own blocks, as you would with
  `memcpy`.
- **The `FAR_NEAR` fallback** uses half of the C heap that's free when `far_init` runs. Call
  `far_init` before your own large `malloc`s if you want it to have the room, or after them if
  your own allocations matter more.
- **Interrupts:** REU transfers run with interrupts disabled for their duration, as cc65's own
  REU drivers do.

To test FarMem on every machine, build the solution and run `Test-FarMem.ps1` beside it. It runs
all seven test programs in VICE and reports which passed.
