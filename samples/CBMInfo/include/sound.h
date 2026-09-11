#ifndef SOUND_H
#define SOUND_H

/*
 * Sound hardware present on this machine - a fixed fact of which chip the
 * target machine actually has (confirmed against cc65's own _sid.h/_ted.h/
 * _vic.h register-layout headers), not something cc65 exposes a runtime
 * probe for - there's no "detect the sound chip" API the way there's a
 * get_ostype()/getcpu() for CPU identity (see machine.h/cpu.h), so this is
 * compile-time knowledge same as machine_address_bits().
 */

const char *sound_chip_name(void);
/* The machine's sound hardware. */

unsigned sound_voice_count(void);
/* How many independent voices/channels that hardware can produce at once. */

#endif
