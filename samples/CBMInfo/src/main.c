#include <main.h>

/* Populates every field of *info by delegating to the machine/cpu/memory/
 * video/sound modules - main.c itself doesn't know or care which of these
 * are read back live at runtime versus fixed per compile-time target; see
 * each module's own header for that distinction. */
void detect_system(SystemInfo *info)
{
    info->model         = machine_model();
    info->address_bits  = machine_address_bits();
    info->word_bits     = machine_word_bits();

    info->cpu           = cpu_name();
    info->cpu_khz       = cpu_speed_khz();

    info->ram_installed_bytes = memory_installed_bytes();
    info->ram_heap_free_bytes = memory_heap_free_bytes();

    info->video          = video_name(detect_video_system());
    video_get_text_size(&info->text_cols, &info->text_rows);
    info->gfx_width      = video_gfx_width();
    info->gfx_height     = video_gfx_height();
    info->colours        = video_colour_count();

    info->sound_chip     = sound_chip_name();
    info->sound_voices   = sound_voice_count();
}

int main(void)
{
    static SystemInfo sys;

    detect_system(&sys);

    clrscr();
    puts("COMMODORE SYSTEM INFORMATION");
    puts("============================");
    puts("");

    printf("Model              : %s\n", sys.model);
    printf("CPU                : %s\n", sys.cpu);
    printf("Clock Speed        : %u.%03u MHz\n", sys.cpu_khz / 1000, sys.cpu_khz % 1000);
    printf("CPU Architecture   : %u-bit processor\n", sys.word_bits);
    printf("Address Bus        : %u-bit\n", sys.address_bits);
    printf("Installed RAM      : %lu KB\n", sys.ram_installed_bytes / 1024);
    printf("Free Heap          : %lu bytes\n", sys.ram_heap_free_bytes);

    printf("Video Standard     : %s\n", sys.video);
    printf("Text Resolution    : %u x %u\n", sys.text_cols, sys.text_rows);
    printf("Graphics Resolution: %ux%u\n", sys.gfx_width, sys.gfx_height);
    printf("Colour Capability  : %u colours\n", sys.colours);

    printf("Sound Hardware     : %s\n", sys.sound_chip);
    printf("Sound Voices       : %u\n", sys.sound_voices);

    return 0;
}
