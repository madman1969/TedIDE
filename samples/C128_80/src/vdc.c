#include <c128.h>
#include "vdc.h"

#define VDC_STATUS_READY   0x80
#define VDC_STATUS_VBLANK  0x20

static void select_reg(unsigned char reg)
{
    VDC.ctrl = reg;
    while (!(VDC.ctrl & VDC_STATUS_READY))
        ;
}

unsigned char vdc_read_reg(unsigned char reg)
{
    select_reg(reg);
    return VDC.data;
}

void vdc_write_reg(unsigned char reg, unsigned char value)
{
    select_reg(reg);
    VDC.data = value;
}

static void set_address(unsigned addr)
{
    vdc_write_reg(VDC_REG_ADDR_HI, addr >> 8);
    vdc_write_reg(VDC_REG_ADDR_LO, (unsigned char)addr);
}

unsigned char vdc_peek(unsigned addr)
{
    set_address(addr);
    return vdc_read_reg(VDC_REG_DATA);
}

void vdc_poke(unsigned addr, unsigned char value)
{
    set_address(addr);
    vdc_write_reg(VDC_REG_DATA, value);
}

void vdc_write_bytes(unsigned addr, const unsigned char* bytes, unsigned char count)
{
    unsigned char i;

    set_address(addr);
    for (i = 0; i < count; ++i)
        vdc_write_reg(VDC_REG_DATA, bytes[i]);
}

void vdc_fill(unsigned addr, unsigned char value, unsigned count)
{
    unsigned char chunk;

    if (count == 0)
        return;

    /* Bit 7 of register 24 clear selects fill rather than copy. */
    vdc_write_reg(VDC_REG_VSCROLL, vdc_read_reg(VDC_REG_VSCROLL) & ~VDC_VSCROLL_COPY);
    vdc_poke(addr, value);
    --count;

    while (count)
    {
        chunk = count > 255 ? 255 : (unsigned char)count;
        vdc_write_reg(VDC_REG_WORD_COUNT, chunk);
        count -= chunk;
    }
}

unsigned vdc_screen_base(void)
{
    return ((unsigned)vdc_read_reg(VDC_REG_DISPLAY_HI) << 8) | vdc_read_reg(VDC_REG_DISPLAY_LO);
}

unsigned vdc_attr_base(void)
{
    return ((unsigned)vdc_read_reg(VDC_REG_ATTR_HI) << 8) | vdc_read_reg(VDC_REG_ATTR_LO);
}

unsigned vdc_charset_base(void)
{
    return (unsigned)(vdc_read_reg(VDC_REG_CHARSET) & 0xE0) << 8;
}

static unsigned cell_offset(unsigned char x, unsigned char y)
{
    return y * VDC_COLUMNS + x;
}

void vdc_set_chars(unsigned char x, unsigned char y, unsigned char code, unsigned count)
{
    vdc_fill(vdc_screen_base() + cell_offset(x, y), code, count);
}

void vdc_set_attrs(unsigned char x, unsigned char y, unsigned char attr, unsigned count)
{
    vdc_fill(vdc_attr_base() + cell_offset(x, y), attr, count);
}

unsigned char vdc_get_attr(unsigned char x, unsigned char y)
{
    return vdc_peek(vdc_attr_base() + cell_offset(x, y));
}

unsigned char vdc_text_charset(void)
{
    return vdc_get_attr(0, 0) & VDC_ATTR_ALTCHARSET;
}

void vdc_wait_vblank(void)
{
    while (VDC.ctrl & VDC_STATUS_VBLANK)
        ;
    while (!(VDC.ctrl & VDC_STATUS_VBLANK))
        ;
}

void vdc_restore_charset(void)
{
    __asm__("jsr $FF62");
}
