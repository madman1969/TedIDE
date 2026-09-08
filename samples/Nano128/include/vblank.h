#ifndef VBLANK_H
#define VBLANK_H

#include <stdint.h>

#define JIFFY (*(volatile uint8_t*)0xA2)

/* Blocks until the KERNAL jiffy clock ticks over - a ~1/60s (NTSC) or
 * ~1/50s (PAL) throttle, not a true VDC vertical-blank wait (the VDC
 * driving 80-column mode is a separate chip from the VIC-II the jiffy
 * IRQ is timed against). Good enough to keep a multi-row repaint from
 * racing far ahead of what the eye can see; not a tear guarantee for
 * the 80-column screen specifically. */
void wait_vblank(void);

#endif