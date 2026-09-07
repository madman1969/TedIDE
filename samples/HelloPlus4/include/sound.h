#ifndef SOUND_H
#define SOUND_H

/* Plays a short arpeggio on TED sound voice 1, then a burst from voice 2's
 * dedicated noise generator - unlike the C64's SID, where any of the three
 * voices can be switched to a noise waveform, TED gives only voice 2 a
 * noise generator, as an alternative to its own square wave.
 * Returns 1 if Q was pressed while it was playing (quit requested). */
unsigned char sound_demo(void);

#endif
