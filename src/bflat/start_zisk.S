.section .text
.global _start
_start:
    # Minimal setup (no args for now)
    li a0, 0        # argc = 0
    li a1, 0        # argv = null
    call __managed__Main
    # Infinite loop (ZisK doesn’t exit traditionally)
    j .