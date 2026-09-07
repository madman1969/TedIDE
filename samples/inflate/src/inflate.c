/*
 * Draws a single character-fill sprite that grows and shrinks ("inflates"
 * and deflates) between 0 and the screen's full size, redrawn each frame
 * from an off-screen buffer until a key is pressed.
 */
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

void main(void)
{
  Screen scrn;
  Sprite sprite;
  
  sprite.width = sprite.height = 0;
  
  // Build the screen buffer
  build_screen(&scrn);  
  
  // Loop until a key is pressed ...
  do{    
    clear_screen(&scrn);   
        
    // Update sprite position and re-draw ...
    update_sprite(&scrn, &sprite);
    draw_sprite(&scrn, &sprite);
    
    draw_screen(&scrn);
  }
  while (!kbhit());
    
  // Free up previously allocated screens ...
  free_screen(&scrn);
}

/*
  Update the sprite
*/
void update_sprite(Screen *scrn, Sprite* sprite)
{
  static int xinc = 1;
  static int yinc = 1;

  // Anchor sprite at 0,0 ...
  sprite->xoffset = sprite->yoffset = 0;
  
  if (sprite->width > scrn->width)
    xinc = -1;
  else if (sprite->width < 1)
    xinc = 1;
  
  if (sprite->height > scrn->height)
    yinc = -1;  
  else if (sprite->height < 1)
    yinc = 1;

  // Assign random character ...
  sprite->c = 0x41 + rand() % 26; 
  
  // Adjust sprite width & height by increment ...
  sprite->width += xinc;
  sprite->height += yinc; 
}

/*
  Draw the specified sprite to the screen buffer
*/
void draw_sprite(Screen *scrn, Sprite *sprite)
{ 
  byte inc;   
  char *p; 
  
  // For each row of the sprite ...
  for (inc = sprite->yoffset; inc < sprite->yoffset + sprite->height; inc++)
  {
    // Draw row ...
    p = scrn->screen + ((inc * scrn->width) + sprite->xoffset);
    memset(p, sprite->c, sprite->width);
  }
} 

/* 
  Allocate screen buffer based on screen size
*/
void build_screen(Screen *scrn)
{  
    /* Clear the screen, hide the cursor, set colors, get screen dimensions */
#if defined(__CBM610__)
      textcolor (COLOR_WHITE);
#elif defined(__VIC20__)
      textcolor (COLOR_WHITE);   /* VIC-20 has no gray colors */
#else
      textcolor (COLOR_GRAY3);
#endif

    bordercolor (COLOR_BLACK);
    bgcolor (COLOR_BLACK);
    clrscr ();
    cursor (0);
    screensize(&scrn->width, &scrn->height);
    

  // Calculate the screen size and allocate memory for buffer ...
  scrn->size = scrn->width * scrn->height;
  scrn->screen = (char *)malloc(scrn->size); 
}

/*
  Clear the screen buffer
*/
void clear_screen(Screen *scrn)
{
  // Fill screen buffer with spaces ...
  memset(scrn->screen, 0x20, scrn->size);  
}

/*
  Free up previously allocatd screen buffer
*/
void free_screen(Screen *scrn)
{
  free(scrn->screen);
}

/*
  Dump the current screen buffer to the screen
*/
void draw_screen(Screen *scrn)
{
  // Copy the screen buffer to the screen
  memcpy((void *)SCRNBASE, scrn->screen, scrn->size);
}