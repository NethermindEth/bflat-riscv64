/**
 * @file
 * @brief C++ shims - just to avoid linking problems in some limited cases.
 *
 * Copyright (C) 2025 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <stdlib.h>

/* operator new must never return null (the C++ contract is "non-null or
 * throw"), but the zkVM build has no exceptions. Returning malloc's null on
 * OOM would defer the failure to a confusing null-deref inside a constructor;
 * fail loudly instead, consistent with the other allocators. */
void* operator new(size_t n)
{
    void *p = malloc(n);
    if (!p)
        exit(255);
    return p;
}

void* operator new[](size_t n)
{
    void *p = malloc(n);
    if (!p)
        exit(255);
    return p;
}
