#ifndef INFLATE_H
#define INFLATE_H

#include <conio.h>
#include <stdlib.h>
#include <string.h>
#include "screen.h"

/*
  Function Prototypes
*/
void build_screen(Screen *scrn);
void clear_screen(Screen *scrn);
void free_screen(Screen *scrn);
void draw_screen(Screen *scrn);
void draw_sprite(Screen *scrn, Sprite *sprite);
void update_sprite(Screen *scrn, Sprite* sprite); 

#endif
