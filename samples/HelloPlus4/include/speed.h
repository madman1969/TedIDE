#ifndef SPEED_H
#define SPEED_H

/* Benchmarks the same busy loop at TED's normal (single-clock) and fast
 * (double-clock) CPU speeds and prints how many real-time jiffies each
 * took - a CPU speed switch unique to the C16/Plus4 among cc65's Commodore
 * targets, since the C64/128's clock rate is fixed by the VIC-II/VDC.
 * Returns 1 if Q was pressed afterwards (quit requested). */
unsigned char speed_demo(void);

#endif
