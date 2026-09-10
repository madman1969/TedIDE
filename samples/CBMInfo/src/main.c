#include <main.h>

static SystemInfo sys;

void detect_system(void)
{
	VideoSystem video;

#if defined(__C64__)

    sys.model        = "Commodore 64";
    sys.cpu          = "MOS 6510";
    sys.cpu_khz      = 1020;
    sys.address_bits = 16;
    sys.word_bits    = 8;
    sys.ram_bytes    = 65536;
    sys.text_cols    = 40;
    sys.text_rows    = 25;
    sys.gfx_width	 = 320;
    sys.gfx_height	 = 200;
    sys.colours      = 16;

#elif defined(__C128__)

    sys.model        = "Commodore 128";
    sys.cpu          = "MOS 8502";
    sys.cpu_khz      = 2000;
    sys.address_bits = 16;
    sys.word_bits    = 8;
    sys.ram_bytes    = 131072;
    sys.text_cols    = 40;
    sys.text_rows    = 25;
	/*
	* VIC-II mode
	*/
	sys.gfx_width = 320;
	sys.gfx_height = 200;
	sys.colours = 16;

#elif defined(__VIC20__)

    sys.model        = "VIC-20";
    sys.cpu          = "MOS 6502";
    sys.cpu_khz      = 1100;
    sys.address_bits = 16;
    sys.word_bits    = 8;
    sys.ram_bytes    = 5120;
    sys.text_cols    = 22;
    sys.text_rows    = 23;
	sys.gfx_width 	 = 176;
	sys.gfx_height 	 = 184;
	sys.colours 	 = 8;

#elif defined(__PLUS4__)

    sys.model        = "Commodore Plus/4";
    sys.cpu          = "MOS 7501";
    sys.cpu_khz      = 1760;
    sys.address_bits = 16;
    sys.word_bits    = 8;
    sys.ram_bytes    = 65536;
    sys.text_cols    = 40;
    sys.text_rows    = 25;
	sys.gfx_width    = 320;
	sys.gfx_height   = 200;    
    sys.colours      = 121;

#elif defined(__C16__)

    sys.model        = "Commodore 16";
    sys.cpu          = "MOS 7501";
    sys.cpu_khz      = 1760;
    sys.address_bits = 16;
    sys.word_bits    = 8;
    sys.ram_bytes    = 16384;
    sys.text_cols    = 40;
    sys.text_rows    = 25;
	sys.gfx_width    = 320;
	sys.gfx_height   = 200;    
    sys.colours      = 121;

#elif defined(__PET__)

    sys.model        = "Commodore PET";
    sys.cpu          = "MOS 6502";
    sys.cpu_khz      = 1000;
    sys.address_bits = 16;
    sys.word_bits    = 8;
    sys.ram_bytes    = 32768;

    sys.text_cols    = 80;
    sys.text_rows    = 25;

    sys.gfx_width    = 0;
    sys.gfx_height   = 0;

    sys.colours      = 1;

    sys.video        = "Monochrome";
    
#endif

	video = detect_video_system();
	sys.video = video_name(video);
}

int main(void)
{
    detect_system();

    clrscr();
    puts("COMMODORE SYSTEM INFORMATION");
    puts("============================");
    puts("");

    printf("Model              : %s\n", sys.model);
    printf("CPU                : %s\n", sys.cpu);
    printf("Clock Speed        : %u.%03u MHz\n", sys.cpu_khz / 1000, sys.cpu_khz % 1000);
    printf("CPU Architecture   : %u-bit processor\n", sys.word_bits);
    printf("Address Bus        : %u-bit\n", sys.address_bits);
    printf("Installed RAM      : %lu KB\n", sys.ram_bytes / 1024);

    printf("Video Standard     : %s\n", sys.video);
    printf("Text Resolution    : %u x %u\n", sys.text_cols, sys.text_rows);

	printf("Graphics Resolution: %ux%u\n", sys.gfx_width, sys.gfx_height);

    printf("Colour Capability  : %u colours\n", sys.colours);

    return 0;
}
