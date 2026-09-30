/* Exercises cc65 runtime stack helpers inside loops, with the C stack pointer at many different
   offsets (odd-sized locals + recursion) so every page-crossing path in pushax/pusha/decsp/incsp
   is taken. Prints and returns a checksum. */
#include <stdio.h>

static unsigned long acc;

static int add3(int a, int b, int c) { return a + b * 3 - c; }
static unsigned char bytes(unsigned char a, unsigned char b) { return a ^ (unsigned char)(b << 1); }
static long mixl(long a, int b) { return a * 3 + b; }

static int deep(unsigned char depth, int seed)
{
    char pad[37];
    int i, s = seed;
    pad[0] = (char)depth;
    pad[36] = (char)seed;
    for (i = 0; i < 20; ++i) {
        s = add3(s, i, pad[0]) ^ bytes((unsigned char)i, (unsigned char)s);
        pad[i % 37] = (char)s;
    }
    if (depth) s += deep(depth - 1, s + pad[5]);
    return s;
}

int main(void)
{
    int i, j;
    unsigned int arr[16];
    for (i = 0; i < 16; ++i) arr[i] = (unsigned)(i * 77);
    for (j = 0; j < 200; ++j) {
        for (i = 0; i < 16; ++i) {
            arr[i] = arr[i] * 3 + (unsigned)add3(i, j, (int)arr[(i + 1) & 15]);
            acc += bytes((unsigned char)arr[i], (unsigned char)j);
        }
        acc += (unsigned long)mixl((long)acc, j);
        acc ^= (unsigned)deep((unsigned char)(j & 7), j);
    }
    printf("%lu\n", acc);
    return (int)(acc & 0x7F);
}
