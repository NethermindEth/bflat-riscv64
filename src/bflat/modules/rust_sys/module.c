/**
 * @file
 * @brief Trivial implementation of Rust compatibility layer.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */

void *__libc_malloc_impl(unsigned long n);
void *__wrap_sys_alloc_aligned(int bytes, int align) {
    return __libc_malloc_impl(bytes);
}
