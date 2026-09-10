#ifndef MAIN_H
#define MAIN_H

#include <stdio.h>
#include <conio.h>
#include <video.h>

typedef struct
{
    const char* model;
    const char* cpu;
    const char* video;

    unsigned cpu_khz;	
	
    unsigned address_bits;
    unsigned word_bits;

    unsigned long ram_bytes;

    unsigned text_cols;
    unsigned text_rows;
    
    unsigned gfx_height;
    unsigned gfx_width;

    unsigned colours;
} SystemInfo;

void print_cpu_speed(unsigned khz);

#endif
