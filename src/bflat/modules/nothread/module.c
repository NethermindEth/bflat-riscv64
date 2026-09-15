/**
 * @file
 * @brief Single-hart replacement for musl's locking and thread primitives
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */

/*
 * The bundled musl libc.a is built rv64ima -- correct for ZisK, which decodes
 * rv64imafd. SP1 and OpenVM decode rv64im, with NO A extension, and musl's
 * locking primitives are where the lr/sc/amo* instructions live: a linked
 * guest picked up 128 of them, all in pthread_cond_timedwait, __lock,
 * __lockfile, pthread_mutex_*, pthread_rwlock_*, __wait and __tl_lock.
 *
 * That is fatal on SP1 and latent on OpenVM. SP1 transpiles every executable
 * word up front and PANICS on one it cannot decode
 * (crates/core/executor/src/disassembler/rrs.rs, transpile(.., false)), so the
 * guest never loads. OpenVM emits `unimp` for such a word and traps only if
 * execution reaches it -- better, but still a landmine.
 *
 * Rebuilding musl for rv64im would be the general fix and belongs in
 * dotnet-riscv. This module is the local one, and it is available because a
 * zkVM guest genuinely has no threads: there is no thread-creation
 * instruction, no interrupts and one hart, so every lock is uncontended by
 * construction and every wait is unwakeable by construction.
 *
 * So: the acquire/release primitives become no-ops that report success, and
 * the primitives that BLOCK become fail-fast. Both halves matter. A no-op lock
 * is not a shortcut -- it is the correct implementation when no second agent
 * exists. A blocking call, by the same argument, can never be woken, so
 * returning from it (with ETIMEDOUT, say) would put the caller in a loop
 * re-checking a condition nothing can change; terminating loudly with a
 * distinct status says what actually happened.
 *
 * HOW IT TAKES EFFECT: not by --wrap. The point is to keep musl's members out
 * of the link entirely, and a wrap leaves them in -- along with their atomics.
 * The linker only extracts an archive member if it defines a still-undefined
 * symbol, so this object is linked ahead of libc.a and defines every global of
 * each member it displaces. That is also why some functions below look
 * pointless (__membarrier_init, __do_cleanup_push): they are not called, they
 * are there so pthread_create.lo is never pulled in for them.
 *
 * Linked for the sp1 and openvm targets only. ZisK decodes the A extension
 * happily, so it keeps musl's real primitives and stays bit-for-bit unchanged.
 */
extern void exit(int status) __attribute__((noreturn));

/*
 * Distinct from nofp's 255 so the two are told apart in a failing run: 255
 * means floating point was reached, 254 means the guest tried to block.
 */
#define NOTHREAD_EXIT_STATUS 254

/*@ // Single abort policy for every blocking primitive: terminate with status
    // 254, never return. exit() resolves to the PAL's __wrap_exit (the
    // target's real termination sequence) in the zkVM link.
    assigns \nothing;
    ensures \false;
    exits \exit_status == NOTHREAD_EXIT_STATUS;
*/
__attribute__((noreturn, noinline, cold))
static void
nothread_trap(void)
{
    exit(NOTHREAD_EXIT_STATUS);
}

/*
 * The ACSL contract is embedded in each macro so every expansion carries one.
 * NOTE: to make Frama-C see annotations inside macro bodies, preprocess with
 * comments preserved through expansion (GCC/Clang: -CC), e.g.
 *   frama-c -cpp-extra-args="-CC" module.c
 */

/** A lock or unlock with nothing to contend against: succeed, do nothing. */
#define NOTHREAD_NOOP(name) \
    /*@ assigns \nothing; */ \
    void name(void *a, void *b, void *c, void *d) \
    { (void)a; (void)b; (void)c; (void)d; }

/** Same, for the pthread surface, whose functions report success as 0. */
#define NOTHREAD_OK(name) \
    /*@ assigns \nothing; ensures \result == 0; */ \
    int name(void *a, void *b, void *c, void *d) \
    { (void)a; (void)b; (void)c; (void)d; return 0; }

/** A primitive that would block. Unwakeable here, so it must not return. */
#define NOTHREAD_BLOCKS(name) \
    /*@ assigns \nothing; ensures \false; \
        exits \exit_status == NOTHREAD_EXIT_STATUS; */ \
    int name(void *a, void *b, void *c, void *d) \
    { (void)a; (void)b; (void)c; (void)d; nothread_trap(); }

/*
 * Declaring every parameter list as four void pointers is deliberate. The RISC-V
 * calling convention passes the first eight integer/pointer arguments in
 * a0..a7, so a definition with more parameters than the caller passes reads
 * registers the caller did not set -- which is harmless precisely because none
 * of these bodies looks at them. It keeps one macro per behaviour instead of
 * one per arity, and it avoids pulling in musl's headers for types (FILE,
 * pthread_mutex_t, struct timespec) that are never dereferenced.
 */

/* --- musl's internal lock, __lock.lo ------------------------------------ */
NOTHREAD_NOOP(__lock)
NOTHREAD_NOOP(__unlock)

/* --- stdio's per-FILE lock, __lockfile.lo -------------------------------
 * __lockfile returns non-zero when the caller took the lock and must release
 * it; stdio's FLOCK/FUNLOCK pair keys off that. Returning 0 says "not held",
 * so the matching __unlockfile is skipped. */
/*@ assigns \nothing; ensures \result == 0; */
int
__lockfile(void *f)
{
    (void)f;
    return 0;
}

NOTHREAD_NOOP(__unlockfile)

/* --- ftrylockfile.lo ---------------------------------------------------- */
NOTHREAD_OK(ftrylockfile)
NOTHREAD_NOOP(__register_locked_file)
NOTHREAD_NOOP(__unlist_locked_file)
NOTHREAD_NOOP(__do_orphaned_stdio_locks)

/* --- buffered stdio -----------------------------------------------------
 * The single-character stdio entry points take the FILE lock inline rather
 * than through __lockfile, so displacing __lockfile is not enough to get their
 * cmpxchg out of the image. musl exports an *_unlocked variant of each from a
 * separate, atomic-free member, and on one hart the two are the same function,
 * so route the locked names to the unlocked ones.
 *
 * fflush is the exception: musl has fflush_unlocked, but in the same member as
 * fflush, so forwarding would drag the atomics back in. It returns success
 * without doing anything, and that is not a compromise on these targets -
 * pal's __wrap___stdio_write fails unconditionally, so the underlying write
 * path is already inert and no buffered byte can leave the FILE either way.
 * In this image fflush is reached only from libunwind's logging paths. */
extern int fputc_unlocked(int c, void *f);
extern int fgetc_unlocked(void *f);
extern int putchar_unlocked(int c);
extern int getchar_unlocked(void);

/*@ assigns \nothing; ensures \result == 0; */
int fflush(void *f) { (void)f; return 0; }

/*@ assigns \nothing; ensures \result == 0; */
int fflush_unlocked(void *f) { (void)f; return 0; }

/*@ assigns \nothing; */
int fputc(int c, void *f) { return fputc_unlocked(c, f); }

/*@ assigns \nothing; */
int putc(int c, void *f) { return fputc_unlocked(c, f); }

/*@ assigns \nothing; */
int _IO_putc(int c, void *f) { return fputc_unlocked(c, f); }

/*@ assigns \nothing; */
int putchar(int c) { return putchar_unlocked(c); }

/*@ assigns \nothing; */
int fgetc(void *f) { return fgetc_unlocked(f); }

/*@ assigns \nothing; */
int getc(void *f) { return fgetc_unlocked(f); }

/*@ assigns \nothing; */
int _IO_getc(void *f) { return fgetc_unlocked(f); }

/*@ assigns \nothing; */
int getchar(void) { return getchar_unlocked(); }

/* --- the futex wait behind a contended lock, __wait.lo ------------------
 * Reachable only when a lock is already held, which cannot happen here. */
NOTHREAD_BLOCKS(__wait)

/* --- mutexes ------------------------------------------------------------ */
NOTHREAD_OK(pthread_mutex_lock)
NOTHREAD_OK(__pthread_mutex_lock)
NOTHREAD_OK(pthread_mutex_unlock)
NOTHREAD_OK(__pthread_mutex_unlock)
NOTHREAD_OK(pthread_mutex_trylock)
NOTHREAD_OK(__pthread_mutex_trylock)
NOTHREAD_OK(__pthread_mutex_trylock_owner)
NOTHREAD_OK(pthread_mutex_timedlock)
NOTHREAD_OK(__pthread_mutex_timedlock)
NOTHREAD_OK(pthread_mutex_consistent)
NOTHREAD_OK(mtx_lock)
NOTHREAD_OK(mtx_trylock)

/* --- condition variables ------------------------------------------------
 * Signal and broadcast are no-ops with nobody to wake; waiting is the one
 * that cannot be honoured. */
NOTHREAD_OK(pthread_cond_signal)
NOTHREAD_OK(pthread_cond_broadcast)
NOTHREAD_OK(pthread_cond_destroy)
NOTHREAD_BLOCKS(pthread_cond_timedwait)
NOTHREAD_BLOCKS(__pthread_cond_timedwait)
NOTHREAD_BLOCKS(__private_cond_signal)

/* --- rwlocks ------------------------------------------------------------ */
NOTHREAD_OK(pthread_rwlock_timedrdlock)
NOTHREAD_OK(__pthread_rwlock_timedrdlock)
NOTHREAD_OK(pthread_rwlock_timedwrlock)
NOTHREAD_OK(__pthread_rwlock_timedwrlock)
NOTHREAD_OK(pthread_rwlock_tryrdlock)
NOTHREAD_OK(__pthread_rwlock_tryrdlock)
NOTHREAD_OK(pthread_rwlock_trywrlock)
NOTHREAD_OK(__pthread_rwlock_trywrlock)
NOTHREAD_OK(pthread_rwlock_unlock)
NOTHREAD_OK(__pthread_rwlock_unlock)

/* --- spinlocks and semaphores ------------------------------------------ */
NOTHREAD_OK(pthread_spin_lock)
NOTHREAD_OK(pthread_spin_trylock)
NOTHREAD_OK(sem_post)
NOTHREAD_OK(sem_trywait)
NOTHREAD_BLOCKS(sem_timedwait)

/* --- barriers ----------------------------------------------------------- */
NOTHREAD_OK(pthread_barrier_destroy)
NOTHREAD_BLOCKS(pthread_barrier_wait)

/* --- the vm lock musl takes around mmap/munmap, vmlock.lo --------------- */
NOTHREAD_NOOP(__vm_lock)
NOTHREAD_NOOP(__vm_unlock)
NOTHREAD_NOOP(__vm_wait)

/* musl's vmlock.lo also exports the lock words themselves; something else in
 * libc takes their address, so the storage has to exist even though nothing
 * ever contends on it. */
int __vmlock_lockptr[2];

/* --- thread lifecycle, pthread_create.lo -------------------------------
 * pthread_create itself is already diverted by the PAL (--wrap=pthread_create),
 * so this definition exists to keep the member -- and the thread-list lock's
 * atomics with it -- out of the link. The rest of the member's globals follow
 * for the same reason. */
NOTHREAD_BLOCKS(pthread_create)
NOTHREAD_BLOCKS(__pthread_create)

/* Exiting "a thread" in a program that has exactly one is exiting the program,
 * and that path belongs to the PAL, not here. */
NOTHREAD_BLOCKS(pthread_exit)
NOTHREAD_BLOCKS(__pthread_exit)

/* The thread-list lock: musl's __tl_lock is precisely one of the atomic
 * sequences this module exists to displace. */
NOTHREAD_NOOP(__tl_lock)
NOTHREAD_NOOP(__tl_unlock)
NOTHREAD_NOOP(__tl_sync)
NOTHREAD_NOOP(__do_cleanup_push)
NOTHREAD_NOOP(__do_cleanup_pop)
NOTHREAD_NOOP(__membarrier_init)

/* NOT defined here, though pthread_create.lo exports them too:
 * __acquire_ptc / __release_ptc also live in lock_ptc.lo and
 * __pthread_tsd_run_dtors in pthread_key_create.lo, and both of those members
 * are pulled in on their own account (the runtime uses TLS keys) and carry no
 * atomics. Defining them here would collide with the copy that is legitimately
 * linked. Leaving them out is safe: they are the only globals of
 * pthread_create.lo not defined above, so nothing can pull that member in for
 * them either - they resolve from the member that is already there. */
