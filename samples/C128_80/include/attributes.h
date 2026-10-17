#ifndef ATTRIBUTES_H
#define ATTRIBUTES_H

/* Shows the four hardware attributes in the top half of each VDC attribute
 * byte - blink, underline, reverse and the alternate character set - one at
 * a time and then all together. Returns DEMO_QUIT if Q was pressed. */
unsigned char attributes_demo(void);

#endif
