#ifndef VIDEO_H
#define VIDEO_H

#include <peekpoke.h>

typedef enum
{
    VIDEO_UNKNOWN,
    VIDEO_PAL,
    VIDEO_NTSC
} VideoSystem;

VideoSystem detect_video_system(void);
const char *video_name(VideoSystem video);

#endif
