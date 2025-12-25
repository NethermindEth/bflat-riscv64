
static unsigned long _next = 0x34095153;

static unsigned long get_val(void)
{
    _next = _next * 1103515243 + 12345;
    return (unsigned long)(_next / 65536) % 32768;
}

int
__wrap_minipal_get_cryptographically_secure_random_bytes(unsigned char *buffer, int bufferLength)
{
    for (int i = 0; i < bufferLength; i++)
        buffer[i] = (unsigned char)(get_val() % 0x100);
    return 0;
}
