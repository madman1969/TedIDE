#ifndef CHARSET_H
#define CHARSET_H

/* A character set with hand-drawn map tiles. The VIC-II can only read a character set from
 * the 16K video bank it's showing, and in the C64's default bank ($0000-$3FFF) the free 2K
 * slots are taken by this program's code. So the screen and character set move to bank 3,
 * into the RAM under the KERNAL ROM: the CPU writes there straight through the ROM, cc65 never
 * uses it, and the video chip always sees RAM. */

#define CHARSET_SCREEN   0xE000   /* the text screen, 1000 bytes */
#define CHARSET_CHARS    0xE800   /* the character set, 2K */

/* The character codes of the map tiles (TILE_DEEP up to TILE_TOWN, in order) and the cursor.
 * They replace reversed characters, which no text here uses. */
#define CHARSET_FIRST_TILE  0x80
#define CHARSET_CURSOR      (CHARSET_FIRST_TILE + 9)

/* Copies the ROM's uppercase character set to CHARSET_CHARS, draws the tiles over their codes,
 * and points the video chip, the KERNAL and conio at the new screen. */
void charset_init(void);

/* Puts the screen and character set back as they were. */
void charset_done(void);

#endif
