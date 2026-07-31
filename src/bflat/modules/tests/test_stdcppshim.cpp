/**
 * @file
 * @brief Unit tests for stdcppshim/module.cpp. Linked with
 *        -Wl,--wrap=malloc so the OOM path ("never return null - fail
 *        loudly with exit 255") is reachable on demand.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <cstddef>
#include <cstring>

#include "common.h"

extern "C" void *__real_malloc(size_t n);

static int malloc_fail;
static size_t last_malloc_size;

extern "C" void *__wrap_malloc(size_t n)
{
    last_malloc_size = n;
    if (malloc_fail)
        return nullptr;
    return __real_malloc(n);
}

int main()
{
    /* success: non-null, request forwarded verbatim, memory usable */
    {
        char *p = static_cast<char *>(operator new(64));
        CHECK(p != nullptr);
        CHECK(last_malloc_size == 64);
        memset(p, 0x5A, 64);
        CHECK(p[0] == 0x5A && p[63] == 0x5A);

        char *q = static_cast<char *>(operator new[](128));
        CHECK(q != nullptr);
        CHECK(last_malloc_size == 128);
        memset(q, 0xA5, 128);
        t_pass++; /* both blocks writable end to end */
    }

    /* OOM: no null return ever - loud exit 255, same as the other
     * allocators in the image */
    EXPECT_EXIT(255, {
        malloc_fail = 1;
        operator new(32);
    });
    EXPECT_EXIT(255, {
        malloc_fail = 1;
        operator new[](32);
    });

    TEST_MAIN_END("stdcppshim");
}
