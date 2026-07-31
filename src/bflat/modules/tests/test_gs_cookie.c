/**
 * @file
 * @brief Unit test for gs_cookie/module.c: the module defines no
 *        functions, only the writable security cookie. Verify it exists,
 *        starts at 0 (so it lands in .data, never .rodata - the whole
 *        point of the module) and is genuinely writable.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include "common.h"

extern volatile unsigned long __wrap___security_cookie;

int main(void)
{
    CHECK(__wrap___security_cookie == 0);
    __wrap___security_cookie = 0xDEADBEEFCAFEBABEUL;
    CHECK(__wrap___security_cookie == 0xDEADBEEFCAFEBABEUL);
    __wrap___security_cookie = 0;
    CHECK(__wrap___security_cookie == 0);

    TEST_MAIN_END("gs_cookie");
}
