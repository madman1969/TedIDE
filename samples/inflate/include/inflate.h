#ifndef INFLATE_H
#define INFLATE_H

#include <conio.h>
#include <stdlib.h>
#include <string.h>
#include "screen.h"

/*
  Function Prototypes

  build_screen  - sets up the display and allocates the off-screen buffer
  clear_screen  - fills the buffer with spaces
  free_screen   - releases the buffer
  draw_screen   - copies the buffer to screen memory
  draw_sprite   - draws a sprite into the buffer
  update_sprite - grows or shrinks the sprite by one step and picks its character
*/
void build_screen(Screen *scrn);
void clear_screen(Screen *scrn);
void free_screen(Screen *scrn);
void draw_screen(Screen *scrn);
void draw_sprite(Screen *scrn, Sprite *sprite);
void update_sprite(Screen *scrn, Sprite* sprite); 

#endif
