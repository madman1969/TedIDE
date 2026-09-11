#ifndef VIDEO_H
#define VIDEO_H

typedef enum
{
    VIDEO_UNKNOWN,
    VIDEO_PAL,
    VIDEO_NTSC,
    VIDEO_MONOCHROME
} VideoSystem;

VideoSystem detect_video_system(void);
/* Determines the actual PAL/NTSC video standard in effect right now, by
 * polling the video chip's own raster line counter (see video.c) rather
 * than assuming one from the compile-time target - VIDEO_UNKNOWN if this
 * platform's video hardware doesn't expose a register this can read (see
 * video.c's own per-platform comments), VIDEO_MONOCHROME for platforms
 * with no colour video standard to speak of (the PET). */

const char *video_name(VideoSystem video);

void video_get_text_size(unsigned *cols, unsigned *rows);
/* The *actual* current text screen size, read back live via conio.h's
 * screensize() - unlike everything else in this header, this reflects
 * whatever mode the machine is in right now (e.g. the C128's 40/80 column
 * switch), not a fixed per-platform assumption. */

unsigned video_gfx_width(void);
unsigned video_gfx_height(void);
unsigned video_colour_count(void);
/* Graphics resolution/colour depth of this machine's video hardware - a
 * fixed fact of the video chip itself, not something read back at runtime
 * (there's no bitmap mode active here to query - see video.c). */

#endif
