#include <memory.h>
#include <stdlib.h>

unsigned long memory_installed_bytes(void)
{
#if defined(__C64__) || defined(__PLUS4__)
    return 65536UL;
#elif defined(__C128__)
    return 131072UL;
#elif defined(__VIC20__)
    return 5120UL;
#elif defined(__C16__)
    return 16384UL;
#elif defined(__PET__)
    return 32768UL;
#elif defined(__CBM510__) || defined(__CBM610__)
    /* Baseline figure for both - the CBM-II "B" series (610/620) actually
     * shipped in several RAM configurations (128K-1M) across its
     * production run, so this is representative rather than exact for
     * every unit. */
    return 131072UL;
#else
    return 0UL;
#endif
}

unsigned long memory_heap_free_bytes(void)
{
    /* _heapmemavail() is the sum of every free heap block, not just the
     * largest one (that's _heapmaxavail() instead) - closer to "how much
     * memory is actually left" than to "how big could my next single
     * allocation be". */
    return (unsigned long)_heapmemavail();
}
