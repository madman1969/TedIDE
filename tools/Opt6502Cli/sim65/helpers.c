/* Makes cc65 call the less common runtime helpers RuntimeInliner inlines (tosicmp, the
   addeq/subeq, decspN/incspN, incaxN, aslax, mulaxN, ldaxi/ldaxidx, leaaxsp, staxspidx and laddeq
   families) inside loops, with operands chosen to hit their edge paths: signed compares that
   overflow, carries into the high byte, and the C stack crossing pages. Prints and returns a
   checksum - which must never include the address of anything static: inlining changes the code
   size, so it moves the data segment. (The C stack's addresses are fine: sim6502 puts it at a
   fixed place at the top of memory.) */
#include <stdio.h>

static unsigned long acc;
static int tab[64];
static int *pt;
static long lg[4];

static void mix(unsigned v) { acc = acc * 31 + v; }

static void block(int n, int seed)
{
    int i;
    for (i = 0; i < n; ++i) {
        char five[5];
        int x = seed * 9 + i;           /* mulax9 */
        int y = i * 5 - seed * 3;       /* mulax5, mulax3 */
        int big = i * 1700 - seed * 900;  /* wraps past +-32767: tosicmp's overflow path */
        int small = -i * 1300 + seed;
        five[0] = (char)x;
        five[4] = (char)y;
        x += y;                         /* addeqysp */
        y -= x;                         /* subeqysp */
        mix((unsigned)(x < y));         /* tosltax etc. (which call tosicmp) */
        mix((unsigned)(big < small));
        mix((unsigned)(big > small));
        mix((unsigned)(x <= big));
        if (big < small) acc += 3;      /* tosicmp + branch on its flags */
        if (x > big) acc ^= 5;
        if (small >= y) acc += 7;
        if (big <= x) acc -= 11;
        tab[(unsigned)i & 63] = x << 2; /* shlax2 */
        tab[(unsigned)(i + 1) & 63] = y << 1;  /* shlax1 */
        pt = &tab[(unsigned)i & 63];
        mix((unsigned)*pt);             /* ldaxi */
        mix((unsigned)pt[3]);           /* incax6 / ldaxidx */
        mix((unsigned)(pt + 4 - tab));  /* incax8 - as an offset: tab's address moves with code size */
        mix((unsigned)(&five[1]));      /* leaaxsp */
        *pt = x ^ y;                    /* staxspidx */
        lg[i & 3] += x;
        lg[(i + 1) & 3] += 1;           /* laddeq1 */
        lg[(i + 2) & 3] += (unsigned char)five[0];  /* laddeqa */
        lg[(i + 3) & 3] += 70000L;      /* laddeq */
        mix((unsigned)five[0] + (unsigned char)five[4]);
    }
}

static void deep(unsigned char depth)
{
    char pad[23];                       /* odd frame so sp crosses pages at varying offsets */
    pad[0] = (char)depth;
    pad[22] = (char)(depth * 7);
    block(12, depth * 101 + pad[0] - pad[22]);
    if (depth) deep(depth - 1);
}

int main(void)
{
    int j;
    for (j = 0; j < 40; ++j) {
        deep((unsigned char)(j % 9));
        mix((unsigned)lg[j & 3] ^ (unsigned)(lg[(j + 1) & 3] >> 16));
    }
    printf("%lu\n", acc);
    return (int)(acc & 0x7F);
}
