#include <string.h>
#include <conio.h>
#include <c64.h>
#include "farmem.h"
#include "charset.h"

/* The tiles, 8x8 pixels each, one byte per row with bit 7 the leftmost pixel. A 1 shows in
 * the cell's colour from view.c, a 0 in the black background. */
static const unsigned char tiles[][8] = {
    { 0xFF, 0xF3, 0xCC, 0xFF, 0xFF, 0x3F, 0xCF, 0xFF },   /* deep sea: waves */
    { 0xFF, 0xCF, 0x30, 0xFF, 0xFF, 0xFC, 0x03, 0xFF },   /* sea: lighter waves */
    { 0xFF, 0xDF, 0xFF, 0xFB, 0xFF, 0xBF, 0xFF, 0xF7 },   /* beach: sand */
    { 0xFF, 0xAF, 0xDF, 0xFF, 0xFF, 0xFA, 0xFD, 0xFF },   /* grass: tufts */
    { 0x42, 0xE7, 0xFF, 0xFF, 0x7E, 0x24, 0x24, 0x00 },   /* forest: two trees */
    { 0x00, 0x00, 0x30, 0x7B, 0xFF, 0xFF, 0xFF, 0xFF },   /* hills: two humps */
    { 0x10, 0x38, 0x38, 0x7C, 0x7C, 0xFE, 0xFE, 0xFF },   /* mountain: a peak */
    { 0x10, 0x38, 0x7C, 0x7C, 0xFE, 0xFE, 0xFF, 0xFF },   /* snow: a broader peak */
    { 0x18, 0x3C, 0x7E, 0xFF, 0x7E, 0x66, 0x66, 0x7E },   /* town: a house */
    { 0xFF, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0xFF },   /* cursor: a frame */
};

#define CHARS        ((unsigned char*)CHARSET_CHARS)
#define CHAR_ROM     ((unsigned char*)0xD000)
#define CPU_PORT     (*(unsigned char*)0x0001)
#define HIBASE       (*(unsigned char*)0x0288)   /* KERNAL: the screen's page */
#define CASE_LOCK    (*(unsigned char*)0x0291)   /* KERNAL: $80 stops C=+Shift changing case */

/* The VIC-II's memory pointers for bank 3: screen at $E000 (bank offset $2000, 8 x 1K) and
 * characters at $E800 (offset $2800, 5 x 2K). */
#define VIC_ADDR_BANK3  ((((CHARSET_SCREEN - 0xC000) / 0x400) << 4) | (((CHARSET_CHARS - 0xC000) / 0x800) << 1))

/* SuperCPU registers: by default it only copies writes to the usual screen at $0400 through to
 * the C64's own RAM, which the video chip reads, so it has to be told to copy all of them. */
#define SCPU_ENABLE_REGS   (*(unsigned char*)0xD07E)
#define SCPU_MIRROR_ALL    (*(unsigned char*)0xD077)
#define SCPU_DISABLE_REGS  (*(unsigned char*)0xD07F)

static unsigned char saved_vic_addr;

void charset_init(void)
{
    unsigned char port;

    if (far_backend() == FAR_SCPU)
    {
        SCPU_ENABLE_REGS = 0;
        SCPU_MIRROR_ALL = 0;
        SCPU_DISABLE_REGS = 0;
    }

    /* The character ROM is only visible to the CPU with the I/O area switched out, so
     * interrupts (whose handler needs I/O) are off while it's copied. */
    __asm__("sei");
    port = CPU_PORT;
    CPU_PORT = port & ~0x04;
    memcpy(CHARS, CHAR_ROM, 0x800);
    CPU_PORT = port;
    __asm__("cli");

    memcpy(CHARS + CHARSET_FIRST_TILE * 8, tiles, sizeof(tiles));

    /* Clear the new screen before the video chip shows it: it's whatever was in that RAM. The
     * KERNAL - and so conio - finds the screen through HIBASE. */
    HIBASE = CHARSET_SCREEN >> 8;
    CASE_LOCK = 0x80;
    clrscr();

    saved_vic_addr = VIC.addr;
    VIC.addr = VIC_ADDR_BANK3;
    CIA2.pra &= 0xFC;           /* bank 3: the bank bits are inverted */
}

void charset_done(void)
{
    CIA2.pra |= 0x03;           /* bank 0 */
    VIC.addr = saved_vic_addr;
    HIBASE = 0x04;
    CASE_LOCK = 0;
}
