/**
 * @file
 * @brief Security cookie implementation for .NET / zkVM to avoid .rodata contamination.
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
__attribute__((section(".data")))
volatile unsigned long __wrap___security_cookie = 0;
