/**
 * @file
 * @brief Unit tests for nothread/module.c.
 *
 * The module has exactly two behaviours and the split between them is the
 * whole design, so that is what is tested: a primitive that acquires or
 * releases must succeed silently (there is no second agent to contend with),
 * and a primitive that would block must terminate with status 254 (there is
 * no second agent to wake it, so returning would strand the caller in a loop
 * re-checking a condition nothing can change).
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include "common.h"

/* The POSIX surface is declared by the real headers on the host, so use those
 * rather than redeclaring it: that is also the stronger check, since it is the
 * host prototypes the module's four-ignored-pointers definitions have to stay
 * ABI-compatible with. */
#include <pthread.h>
#include <semaphore.h>

/* musl internals have no public header. */
extern void __lock(volatile int *l);
extern void __unlock(volatile int *l);
extern int __lockfile(void *f);
extern void __unlockfile(void *f);
extern void __do_orphaned_stdio_locks(void);
extern int __wait(volatile int *addr, volatile int *waiters, int val, int priv);
extern int __private_cond_signal(void *c, int n);
extern void __tl_lock(void);
extern void __tl_unlock(void);
extern void __vm_lock(void);
extern void __vm_unlock(void);
extern int mtx_lock(void *m);
extern int fflush(FILE *f);
extern int fputc(int c, FILE *f);
extern int putc(int c, FILE *f);
extern int putchar(int c);
extern int fgetc(FILE *f);
extern int getc(FILE *f);

typedef int (*blocking_fn)(void);

static const struct {
    const char *name;
    blocking_fn fn;
} blocking[] = {
    { "__wait",                 (blocking_fn)__wait },
    { "pthread_cond_timedwait", (blocking_fn)pthread_cond_timedwait },
    { "__private_cond_signal",  (blocking_fn)__private_cond_signal },
    { "sem_timedwait",          (blocking_fn)sem_timedwait },
    { "pthread_barrier_wait",   (blocking_fn)pthread_barrier_wait },
    { "pthread_create",         (blocking_fn)pthread_create },
    { "pthread_exit",           (blocking_fn)pthread_exit },
};

int main(void)
{
    /* A lock word the no-ops are handed, to prove they leave it alone: musl's
     * callers treat a non-zero value as "held", so a stub that helpfully
     * cleared it would change stdio's behaviour rather than preserve it. */
    volatile int lockword = 0x5a5a5a5a;
    pthread_mutex_t mtx;
    pthread_cond_t cond;
    pthread_rwlock_t rw;
    pthread_spinlock_t spin;
    sem_t sem;
    struct timespec ts = { 0, 0 };
    FILE *tmp = tmpfile();

    CHECK(tmp != NULL);

    __lock(&lockword);
    CHECK(lockword == 0x5a5a5a5a);
    __unlock(&lockword);
    CHECK(lockword == 0x5a5a5a5a);

    /* __lockfile must report "not acquired" so stdio's FLOCK/FUNLOCK pair
     * skips the matching unlock. */
    CHECK(__lockfile((void *)0) == 0);
    __unlockfile((void *)0);
    __do_orphaned_stdio_locks();
    CHECK(ftrylockfile(stdout) == 0);

    /* Uncontended by construction: every acquire succeeds. */
    CHECK(pthread_mutex_lock(&mtx) == 0);
    CHECK(pthread_mutex_unlock(&mtx) == 0);
    CHECK(pthread_mutex_trylock(&mtx) == 0);
    CHECK(pthread_mutex_timedlock(&mtx, &ts) == 0);
    CHECK(pthread_rwlock_tryrdlock(&rw) == 0);
    CHECK(pthread_rwlock_trywrlock(&rw) == 0);
    CHECK(pthread_rwlock_timedrdlock(&rw, &ts) == 0);
    CHECK(pthread_rwlock_timedwrlock(&rw, &ts) == 0);
    CHECK(pthread_rwlock_unlock(&rw) == 0);
    CHECK(pthread_spin_lock(&spin) == 0);
    CHECK(sem_post(&sem) == 0);
    CHECK(sem_trywait(&sem) == 0);
    CHECK(mtx_lock((void *)0) == 0);

    /* Signalling with nobody waiting is a no-op, not a failure. */
    CHECK(pthread_cond_signal(&cond) == 0);
    CHECK(pthread_cond_broadcast(&cond) == 0);
    CHECK(pthread_cond_destroy(&cond) == 0);

    /* stdio: the locked names must behave as their unlocked twins, and
     * fflush must report success without touching anything. */
    CHECK(fflush(stdout) == 0);
    CHECK(fputc('x', tmp) == 'x');
    CHECK(putc('y', tmp) == 'y');
    CHECK(putchar('\n') == '\n');
    rewind(tmp);
    CHECK(fgetc(tmp) == 'x');
    CHECK(getc(tmp) == 'y');

    __tl_lock();
    __tl_unlock();
    __vm_lock();
    __vm_unlock();

    for (unsigned i = 0; i < sizeof(blocking) / sizeof(blocking[0]); i++) {
        pid_t pid = fork();
        if (pid == 0) {
            blocking[i].fn();
            _exit(111); /* must never return */
        }
        int st;
        waitpid(pid, &st, 0);
        if (WIFEXITED(st) && WEXITSTATUS(st) == 254) {
            t_pass++;
        } else {
            t_fail++;
            fprintf(stderr, "FAIL: %s: expected exit 254, got %s %d\n",
                    blocking[i].name,
                    WIFEXITED(st) ? "exit" : "signal",
                    WIFEXITED(st) ? WEXITSTATUS(st) : WTERMSIG(st));
        }
    }

    TEST_MAIN_END("nothread");
}
