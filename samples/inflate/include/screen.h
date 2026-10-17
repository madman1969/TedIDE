#ifndef SCREEN_H
#define SCREEN_H

/* Writes one byte straight to a memory address. */
#define POKE(addr,val) (*(unsigned char*) (addr) = (val))

/* Where each machine's text screen memory starts - draw_screen() copies the
 * off-screen buffer straight to it. */

#if defined(__C64__)
#define SCRNBASE 0x0400   /* C64 text screen */

#elif defined(__PET__)
#define SCRNBASE 0x8000   /* PET text screen */

#elif defined(__C128__)
#define SCRNBASE 0x0400   /* VIC-IIe 40-column mode */

#elif defined(__C16__)
#define SCRNBASE 0x0C00   /* TED text screen */

#elif defined(__VIC20__)
#define SCRNBASE 0x1E00   /* cc65 default: unexpanded VIC-20 */

#endif

typedef unsigned char byte;

/* A filled rectangle of one character: its top-left corner, its size in
 * characters, and the screen code it's filled with. */
typedef struct {
  byte xoffset;
  byte yoffset;
  byte width;
  byte height;
  char c;
} Sprite;

// Structure used for double-buffering: an off-screen copy of the text
// screen (width x height screen codes, size bytes in all), drawn into and
// then copied to screen memory in one go so the screen never shows a
// half-drawn frame.
typedef struct {
  byte width;
  byte height;
  int size;
  char *screen;
} Screen;

#endif