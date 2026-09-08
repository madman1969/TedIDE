#ifndef SCREEN_H
#define SCREEN_H

#define POKE(addr,val) (*(unsigned char*) (addr) = (val))

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

typedef struct {
  byte xoffset;
  byte yoffset;
  byte width;
  byte height;
  char c;  
} Sprite;

// Structure used for double-buffering
typedef struct {
  byte width;
  byte height;
  int size;
  char *screen;
} Screen;

#endif