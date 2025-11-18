/**
 * @file
 * @brief C++ shims - just to avoid linking problems in some limited cases.
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <stdlib.h>

void* operator new(size_t n) noexcept
{
    return malloc(n);
}

void* operator new[](size_t n) noexcept
{
    return malloc(n);
}
