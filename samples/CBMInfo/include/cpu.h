#ifndef CPU_H
#define CPU_H

/*
 * CPU identification - the chip family is queried at runtime via getcpu()
 * (6502.h, available on every cc65 target) and only then named, rather than
 * just assuming the platform's usual chip - see cpu_name()'s own comment
 * for why that still needs a per-platform lookup afterwards. The C128's
 * clock speed is genuinely read back at runtime too (get_c128_speed(),
 * accelerator.h), since it's the one target here that can actually change
 * speed under program control.
 */

const char *cpu_name(void);
/* Detected CPU model name. */

unsigned cpu_speed_khz(void);
/* Current CPU clock speed, in kHz - dynamically read back on the C128
 * (1000 or 2000, depending on its current 1/2 MHz mode), a fixed nominal
 * figure everywhere else (cc65 has no way to measure an unknown clock rate
 * at runtime without an external time reference). */

#endif
