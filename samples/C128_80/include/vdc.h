#ifndef VDC_H
#define VDC_H

/* Direct access to the C128's VDC (8563) chip through its two ports at
 * $D600/$D601. The VDC has its own 16KB (or 64KB) of RAM that the CPU can't
 * see - every read and write goes through register 31 at the address held
 * in registers 18/19. Shared by all the graphical demos. */

/* VDC registers used by the demos. */
#define VDC_REG_DISPLAY_HI   12  /* screen RAM start */
#define VDC_REG_DISPLAY_LO   13
#define VDC_REG_ADDR_HI      18  /* update address for register 31 */
#define VDC_REG_ADDR_LO      19
#define VDC_REG_ATTR_HI      20  /* attribute RAM start */
#define VDC_REG_ATTR_LO      21
#define VDC_REG_VSCROLL      24  /* bit 7 copy/fill, bit 6 reverse screen, bits 0-4 vertical scroll */
#define VDC_REG_HSCROLL      25  /* bit 7 bitmap mode, bit 6 attributes on, bits 0-3 horizontal scroll */
#define VDC_REG_CHARSET      28  /* bits 5-7 character set address / 8KB */
#define VDC_REG_WORD_COUNT   30  /* writing N repeats the last data byte N more times */
#define VDC_REG_DATA         31

#define VDC_VSCROLL_COPY     0x80
#define VDC_VSCROLL_REVERSE  0x40

/* Attribute byte: a colour in bits 0-3, plus four display attributes. */
#define VDC_ATTR_BLINK       0x10
#define VDC_ATTR_UNDERLINE   0x20
#define VDC_ATTR_REVERSE     0x40
#define VDC_ATTR_ALTCHARSET  0x80

/* The VDC's 16 RGBI colours (these are not the VIC-II's COLOR_ values). */
#define VDC_BLACK            0
#define VDC_DARK_GREY        1
#define VDC_DARK_BLUE        2
#define VDC_LIGHT_BLUE       3
#define VDC_DARK_GREEN       4
#define VDC_LIGHT_GREEN      5
#define VDC_DARK_CYAN        6
#define VDC_LIGHT_CYAN       7
#define VDC_DARK_RED         8
#define VDC_LIGHT_RED        9
#define VDC_DARK_PURPLE      10
#define VDC_LIGHT_PURPLE     11
#define VDC_DARK_YELLOW      12
#define VDC_LIGHT_YELLOW     13
#define VDC_LIGHT_GREY       14
#define VDC_WHITE            15

#define VDC_COLUMNS          80
#define VDC_ROWS             25

unsigned char vdc_read_reg(unsigned char reg);
void vdc_write_reg(unsigned char reg, unsigned char value);

/* Single bytes and runs of bytes in VDC RAM. */
unsigned char vdc_peek(unsigned addr);
void vdc_poke(unsigned addr, unsigned char value);
void vdc_write_bytes(unsigned addr, const unsigned char* bytes, unsigned char count);

/* Fills count bytes with value using the VDC's own block-fill hardware, so
 * the CPU only writes the first byte and a repeat count. */
void vdc_fill(unsigned addr, unsigned char value, unsigned count);

/* Where the screen, attribute and character RAM currently are. */
unsigned vdc_screen_base(void);
unsigned vdc_attr_base(void);
unsigned vdc_charset_base(void);

/* Screen codes and attributes by text cell, running along the row. */
void vdc_set_chars(unsigned char x, unsigned char y, unsigned char code, unsigned count);
void vdc_set_attrs(unsigned char x, unsigned char y, unsigned char attr, unsigned count);
unsigned char vdc_get_attr(unsigned char x, unsigned char y);

/* The alternate-charset bit conio is printing with (read back from the
 * banner), so the demos' own attributes keep text in the same case. */
unsigned char vdc_text_charset(void);

/* Waits for the start of the next vertical blank (status bit 5). */
void vdc_wait_vblank(void);

/* Copies the ROM character set back into VDC RAM (KERNAL DLCHR, $FF62). */
void vdc_restore_charset(void);

#endif
