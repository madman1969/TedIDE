#include <video.h>

VideoSystem detect_video_system(void)
{
    unsigned char max_raster = 0;
    unsigned char current;
    unsigned int i;

    for (i = 0; i < 1000; ++i)
    {
        current = PEEK(0xD012);

        if (current > max_raster)
        {
            max_raster = current;
        }
    }

    /*
     * NTSC wraps around near 262 lines.
     * PAL wraps around near 312 lines.
     *
     * Bit 8 of the raster counter is held
     * in register $D011 bit 7.
     */

    if (PEEK(0xD011) & 0x80)
    {
        return VIDEO_PAL;
    }

    return VIDEO_NTSC;
}

const char *video_name(VideoSystem video)
{
  	switch (video)
    {
        case VIDEO_PAL:
            return "PAL";

        case VIDEO_NTSC:
            return "NTSC";

        default:
            return "Unknown";
    }
}