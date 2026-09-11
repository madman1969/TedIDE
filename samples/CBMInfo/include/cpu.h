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

void cpu_detect_fast_mode(void);
/* Probes for whichever CPU-speed accelerator this machine actually has -
 * an add-on cartridge on the C64 (SuperCPU/Turbo Master/C65-in-C64-mode/
 * Chameleon/C64DTV), or the C128's own built-in 1/2 MHz native switch - and
 * switches it to its fastest available mode if one is found, all via
 * cc65's own accelerator.h (real detect_xxx()/set_xxx_speed() calls, not
 * an assumption). Call this once, before cpu_supports_fast_mode(),
 * cpu_fast_mode_description(), and cpu_speed_khz(), so all three reflect
 * whatever this leaves the machine running at. A no-op (and "not
 * supported") on every other target here. */

int cpu_supports_fast_mode(void);
/* Whether cpu_detect_fast_mode() found (and switched on) an accelerator -
 * only meaningful after calling it first. */

const char *cpu_fast_mode_description(void);
/* Short description of whichever accelerator cpu_detect_fast_mode() found,
 * or "Not available on this machine" if none was - only meaningful after
 * calling it first. */

unsigned cpu_speed_khz(void);
/* Current CPU clock speed, in kHz - reflects whatever cpu_detect_fast_mode()
 * already switched the machine to, if it found and engaged an accelerator
 * with a documented exact clock rate; otherwise dynamically read back on
 * the C128 (1000 or 2000, depending on its current 1/2 MHz mode), or a
 * fixed nominal figure everywhere else (cc65 has no way to measure an
 * unknown clock rate at runtime without an external time reference - see
 * cpu_detect_fast_mode()'s own comment for the two accelerators whose
 * "fast" tier has no such documented rate). */

#endif
