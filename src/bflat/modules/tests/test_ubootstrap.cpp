/**
 * @file
 * @brief Unit tests for ubootstrap/module.cpp. The NativeAOT runtime
 *        entry points are stubbed here with capture/fault knobs; the
 *        __modules/__managedcode/__unbox section ranges are recreated by
 *        placing real data into identically named sections (the linker
 *        then provides the __start_/__stop_ symbols the module reads).
 *
 * Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
 */
#include <csignal>
#include <cstdint>
#include <cstring>

#include "common.h"

/* --- section contents standing in for the real image layout --- */
extern "C" {
void *test_module_entry __attribute__((used, section("__modules"))) =
    (void *)0x11;
char managed_blob[4] __attribute__((used, section("__managedcode"))) =
    { 1, 2, 3, 4 };
char unbox_blob[2] __attribute__((used, section("__unbox"))) = { 9, 9 };
}

/* --- runtime stubs with capture/fault knobs --- */
static int fail_init;
static int fail_register;

static void *cap_os_module;
static void *cap_mc_start;
static uint32_t cap_mc_len;
static void *cap_ub_start;
static uint32_t cap_ub_len;
static void **cap_classlib;
static uint32_t cap_n_classlib;
static void **cap_modules;
static int cap_module_count;
static int cap_managed_argc = -1;
static char **cap_managed_argv;

extern "C" bool RhInitialize(bool isDll)
{
    (void)isDll;
    return !fail_init;
}

extern "C" bool RhRegisterOSModule(void *pModule, void *pvManagedCodeStart,
                                   uint32_t cbManagedCode,
                                   void *pvUnboxingStubsStart,
                                   uint32_t cbUnboxingStubs,
                                   void **pClasslibFunctions,
                                   uint32_t nClasslibFunctions)
{
    cap_os_module = pModule;
    cap_mc_start = pvManagedCodeStart;
    cap_mc_len = cbManagedCode;
    cap_ub_start = pvUnboxingStubsStart;
    cap_ub_len = cbUnboxingStubs;
    cap_classlib = pClasslibFunctions;
    cap_n_classlib = nClasslibFunctions;
    return !fail_register;
}

extern "C" void InitializeModules(void *osModule, void **modules, int count,
                                  void **pClasslibFunctions,
                                  int nClasslibFunctions)
{
    (void)osModule;
    (void)pClasslibFunctions;
    (void)nClasslibFunctions;
    cap_modules = modules;
    cap_module_count = count;
}

static char fake_module_handle;
/* C++ linkage: the module declares this one WITHOUT extern "C". */
void *PalGetModuleHandleFromPointer(void *pointer)
{
    (void)pointer;
    return &fake_module_handle;
}

/* The classlib exports whose addresses the module tables up. */
#define STUB(n) \
    extern "C" void n() {}
STUB(GetRuntimeException)
STUB(RuntimeFailFast)
STUB(ThreadEntryPoint)
STUB(AppendExceptionStackFrame)
STUB(ResolveDispatch)
STUB(GetSystemArrayEEType)
STUB(OnFirstChanceException)
STUB(OnUnhandledException)
#undef STUB

extern "C" int __managed__Main(int argc, char *argv[])
{
    cap_managed_argc = argc;
    cap_managed_argv = argv;
    return 123;
}

/* --- module under test --- */
extern "C" int uBootstrap_InitializeRuntime();
extern "C" int uBootstrap_main(int argc, char *argv[]);
extern "C" int g_bootstrap_argc;
extern "C" char **g_bootstrap_argv;

int main()
{
    /* success path: host argc/argv ignored, managed Main gets the fake
     * "app" command line and its status is returned verbatim */
    char *host_argv[] = { (char *)"host", nullptr };
    CHECK(uBootstrap_main(7, host_argv) == 123);
    CHECK(cap_managed_argc == 1);
    CHECK(cap_managed_argv == g_bootstrap_argv);
    CHECK(cap_managed_argv[0] != nullptr
          && strcmp(cap_managed_argv[0], "app") == 0);
    CHECK(cap_managed_argv[1] == nullptr);
    CHECK(g_bootstrap_argc == 1);

    /* registration wiring: module handle from PalGetModuleHandleFromPointer,
     * code/unbox ranges spanning exactly the section contents */
    CHECK(cap_os_module == &fake_module_handle);
    CHECK(cap_mc_start == (void *)managed_blob);
    CHECK(cap_mc_len == sizeof(managed_blob));
    CHECK(cap_ub_start == (void *)unbox_blob);
    CHECK(cap_ub_len == sizeof(unbox_blob));

    /* classlib table: 8 entries, and slots 2/4 (ThreadEntryPoint /
     * ResolveDispatch - called by RhpCidResolve_Worker WITHOUT a null
     * check) must be populated with the right functions */
    CHECK(cap_n_classlib == 8);
    CHECK(cap_classlib != nullptr);
    CHECK(cap_classlib[0] == (void *)&GetRuntimeException);
    CHECK(cap_classlib[1] == (void *)&RuntimeFailFast);
    CHECK(cap_classlib[2] == (void *)&ThreadEntryPoint);
    CHECK(cap_classlib[3] == (void *)&AppendExceptionStackFrame);
    CHECK(cap_classlib[4] == (void *)&ResolveDispatch);
    CHECK(cap_classlib[5] == (void *)&GetSystemArrayEEType);
    CHECK(cap_classlib[6] == (void *)&OnFirstChanceException);
    CHECK(cap_classlib[7] == (void *)&OnUnhandledException);

    /* module list handed to InitializeModules: our single entry */
    CHECK(cap_module_count == 1);
    CHECK(cap_modules != nullptr && cap_modules[0] == (void *)0x11);

    /* repeated init is fine (idempotent success path) */
    CHECK(uBootstrap_InitializeRuntime() == 0);

    /* failure paths are DESIGNED faults: stores to addresses 1/2 mark
     * which init step died - observable as SIGSEGV */
    EXPECT_SIGNAL(SIGSEGV, {
        fail_init = 1;
        uBootstrap_InitializeRuntime();
    });
    EXPECT_SIGNAL(SIGSEGV, {
        fail_register = 1;
        uBootstrap_InitializeRuntime();
    });

    TEST_MAIN_END("ubootstrap");
}
