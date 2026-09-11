#ifndef MACHINE_H
#define MACHINE_H

/*
 * Identifies the physical machine this program is actually running on.
 *
 * cc65 builds a separate binary per target machine (there is no single
 * cross-platform executable), so the base model family is necessarily
 * already known at compile time - the #if defined(__xxx__) blocks in
 * machine.c are exactly that, not "faked" runtime data. Where cc65 exposes
 * a genuine runtime API to go further than that base family, machine_model()
 * calls it and reports the specific hardware/ROM variant actually detected,
 * rather than just repeating the compile-time target name - currently this
 * is only possible on the C64 target, via get_ostype() (c64.h).
 */

const char *machine_model(void);
/* Human-readable model name - refined with runtime-detected variant info
 * where cc65 provides a way to (currently: C64 only). */

unsigned machine_address_bits(void);
/* Width of the CPU's external address bus, in bits - a fixed architectural
 * fact for every target here (all pure 6502-family machines), not
 * something read back from hardware at runtime. */

unsigned machine_word_bits(void);
/* Width of the CPU's native register/data word, in bits - same caveat as
 * machine_address_bits(). */

#endif
