#ifndef MAIN_H
#define MAIN_H

#include <stdio.h>
#include <conio.h>
#include <machine.h>
#include <cpu.h>
#include <memory.h>
#include <video.h>
#include <sound.h>

typedef struct
{
    const char* model;
    const char* cpu;
    const char* video;
    const char* sound_chip;

    unsigned cpu_khz;
    unsigned sound_voices;

    int fast_mode_supported;
    const char* fast_mode_description;
    int supercpu_enabled;

    unsigned address_bits;
    unsigned word_bits;

    unsigned long ram_installed_bytes;
    unsigned long ram_heap_free_bytes;

    unsigned text_cols;
    unsigned text_rows;

    unsigned gfx_height;
    unsigned gfx_width;

    unsigned colours;
} SystemInfo;

/* Populates every SystemInfo field by calling out to machine.h/cpu.h/
 * memory.h/video.h/sound.h's own detection functions - see each header's
 * own doc comment for which fields are genuinely read back at runtime
 * versus fixed per compile-time target (cc65 builds a separate binary per
 * machine, so some of this is necessarily already known at compile time -
 * see machine.h). */
void detect_system(SystemInfo *info);

#endif
