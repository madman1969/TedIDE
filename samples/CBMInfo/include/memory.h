#ifndef MEMORY_H
#define MEMORY_H

/*
 * RAM details. Total installed RAM is a fixed hardware fact for each of
 * these machines - cc65 has no runtime way to probe how much physical RAM
 * is actually installed - but how much of it a running program still has
 * free right now is genuinely dynamic, and cc65's own heap allocator
 * already tracks it (see stdlib.h's _heapmemavail()), so
 * memory_heap_free_bytes() reports the real, current figure instead of
 * just repeating a constant.
 */

unsigned long memory_installed_bytes(void);
/* Total RAM installed in this machine. */

unsigned long memory_heap_free_bytes(void);
/* Bytes currently free on the C heap, measured right now via cc65's own
 * allocator - falls as the program's own data grows, unlike
 * memory_installed_bytes() above. */

#endif
