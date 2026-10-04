unsigned char total;

unsigned char add(unsigned char a, unsigned char b)
{
    unsigned char sum = a + b;
    return sum;
}

int main(void)
{
    unsigned char i = 1;
    total = add(i, 2);
    i = i + total;
    total = i;
    return 0;
}