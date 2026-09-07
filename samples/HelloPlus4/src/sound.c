#include <conio.h>
/* See palette.c for why this is included explicitly. */
#include <plus4.h>
#include "sound.h"
#include "delay.h"
#include "input.h"

/* TED sound control register ($FF11, the snd_ctrl field of the TED struct
 * in <_ted.h>) - low nibble is volume (0-8), these are its mode bits. */
#define SND_VOICE1_ON 0x10
#define SND_VOICE2_ON 0x20
#define SND_NOISE_ON  0x40

#define VOLUME_MAX 0x08

/* Converts a frequency in Hz to TED's 10-bit tone-generator reload value.
 * The generator counts up every cycle and resets (toggling the output)
 * on reaching 1023, reloading from this value each time - so a larger
 * value means fewer cycles between resets, i.e. a higher pitch. The
 * constant below is Commodore's own PAL sound-clock figure for the 264
 * series (264memory.txt / the Plus/4 frequency tables); NTSC machines use
 * 111860 instead, close enough here not to matter for a demo tune. */
static unsigned int freq_to_reg(unsigned int hz)
{
    return (unsigned int)(1024UL - (111840UL / hz));
}

static void voice1_tone(unsigned int hz, unsigned char volume)
{
    unsigned int reg = freq_to_reg(hz);
    TED.snd1_freq_lo = (unsigned char)(reg & 0xFF);
    TED.misc = (unsigned char)((TED.misc & 0xFC) | (reg >> 8));
    TED.snd_ctrl = (unsigned char)(SND_VOICE1_ON | (volume & 0x0F));
}

static void voice2_noise(unsigned char volume)
{
    TED.snd2_freq_lo = 0xC0;
    TED.snd2_freq_hi = 0x00;
    TED.snd_ctrl = (unsigned char)(SND_VOICE2_ON | SND_NOISE_ON | (volume & 0x0F));
}

static void silence(void)
{
    TED.snd_ctrl = 0;
}

static const unsigned int arpeggio_hz[] = { 262, 330, 392, 523 };
#define ARPEGGIO_NOTES (sizeof(arpeggio_hz) / sizeof(arpeggio_hz[0]))

unsigned char sound_demo(void)
{
    unsigned char i;

    textcolor(COLOR_WHITE);
    cputsxy(0, 4, "TED sound: voice 1 (tone) plays a C major arpeggio,");
    cputsxy(0, 5, "then voice 2's dedicated noise generator fires once.");

    for (i = 0; i < ARPEGGIO_NOTES; i++)
    {
        voice1_tone(arpeggio_hz[i], VOLUME_MAX);
        delay_long();
        if (input_quit_requested())
        {
            silence();
            return 1;
        }
    }
    silence();
    delay_short();

    voice2_noise(VOLUME_MAX);
    delay_long();
    silence();

    return input_quit_requested();
}
