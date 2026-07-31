/**
 * @file
 * @brief Simple bootstrap based on .NET bootstrap.
 *
 * Copyright is preserved from dotnet bootstrap (MIT license)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */

#include <stdint.h>

/* Fake command-line arguments for the managed runtime. */
static char s_argv0[] = "app";
static char *s_argv_arr[] = { s_argv0, nullptr };
extern "C" int   g_bootstrap_argc = 1;
extern "C" char **g_bootstrap_argv = s_argv_arr;

#if defined(__APPLE__)

extern void * __modules_a[] __asm("section$start$__DATA$__modules");
extern void * __modules_z[] __asm("section$end$__DATA$__modules");
extern char __managedcode_a __asm("section$start$__TEXT$__managedcode");
extern char __managedcode_z __asm("section$end$__TEXT$__managedcode");
extern char __unbox_a __asm("section$start$__TEXT$__unbox");
extern char __unbox_z __asm("section$end$__TEXT$__unbox");

#else // __APPLE__

extern "C" void * __start___modules[];
extern "C" void * __stop___modules[];
static void * (&__modules_a)[] = __start___modules;
static void * (&__modules_z)[] = __stop___modules;

extern "C" char __start___managedcode;
extern "C" char __stop___managedcode;
static char& __managedcode_a = __start___managedcode;
static char& __managedcode_z = __stop___managedcode;

extern "C" char __start___unbox;
extern "C" char __stop___unbox;
static char& __unbox_a = __start___unbox;
static char& __unbox_z = __stop___unbox;

#endif // __APPLE__

extern "C" bool RhInitialize(bool isDll);
extern "C" void RhSetRuntimeInitializationCallback(int (*fPtr)());

extern "C" bool RhRegisterOSModule(void * pModule,
    void * pvManagedCodeStartRange, uint32_t cbManagedCodeRange,
    void * pvUnboxingStubsStartRange, uint32_t cbUnboxingStubsRange,
    void ** pClasslibFunctions, uint32_t nClasslibFunctions);

void* PalGetModuleHandleFromPointer(void* pointer);

#if defined(HOST_X86) && defined(HOST_WINDOWS)
#define STRINGIFY(s) #s
#define MANAGED_RUNTIME_EXPORT_ALTNAME(_method) \
    STRINGIFY(/alternatename:_##_method=_method)
#define MANAGED_RUNTIME_EXPORT_CALLCONV __cdecl
#define MANAGED_RUNTIME_EXPORT(_name) \
    __pragma(comment (linker, MANAGED_RUNTIME_EXPORT_ALTNAME(_name))) \
    extern "C" void MANAGED_RUNTIME_EXPORT_CALLCONV _name();
#define MANAGED_RUNTIME_EXPORT_NAME(_name) _name
#else
#define MANAGED_RUNTIME_EXPORT_CALLCONV
#define MANAGED_RUNTIME_EXPORT(_name) \
    extern "C" void _name();
#define MANAGED_RUNTIME_EXPORT_NAME(_name) _name
#endif

MANAGED_RUNTIME_EXPORT(GetRuntimeException)
MANAGED_RUNTIME_EXPORT(RuntimeFailFast)
MANAGED_RUNTIME_EXPORT(ThreadEntryPoint)
MANAGED_RUNTIME_EXPORT(AppendExceptionStackFrame)
/* ResolveDispatch (classlib slot 4) is a bflat-fork managed export that only
 * exists in the .NET 11 runtime blobs; the .NET 10 fork leaves slot 4 unused
 * (ICodeManager.h: "unused = 4"), so referencing it there is an undefined
 * symbol at link time. Gate the declaration/reference on the version; only an
 * explicit .NET 10 build drops it (undefined BFLAT_DOTNET defaults to the
 * current net11 layout, keeping bare/test compiles unchanged). */
#if !defined(BFLAT_DOTNET) || BFLAT_DOTNET >= 11
MANAGED_RUNTIME_EXPORT(ResolveDispatch)
#endif
MANAGED_RUNTIME_EXPORT(GetSystemArrayEEType)
MANAGED_RUNTIME_EXPORT(OnFirstChanceException)
MANAGED_RUNTIME_EXPORT(OnUnhandledException)
#ifdef FEATURE_OBJCMARSHAL
MANAGED_RUNTIME_EXPORT(ObjectiveCMarshalTryGetTaggedMemory)
MANAGED_RUNTIME_EXPORT(ObjectiveCMarshalGetIsTrackedReferenceCallback)
MANAGED_RUNTIME_EXPORT(ObjectiveCMarshalGetOnEnteredFinalizerQueueCallback)
MANAGED_RUNTIME_EXPORT(ObjectiveCMarshalGetUnhandledExceptionPropagationHandler)
#endif

typedef void (MANAGED_RUNTIME_EXPORT_CALLCONV *pfn)();

// Mirror the stock libbootstrapper table exactly (see its
// .rela.data.rel.ro._ZL19c_classlibFunctions). The bflat runtime fork
// repurposes the historically-unused slots: 2 = ThreadEntryPoint and
// 4 = ResolveDispatch - RhpCidResolve_Worker fetches slot 4 and CALLS IT
// WITHOUT a null check, so a nullptr here turns the first interface
// dispatch that misses the cache (e.g. string.Format's ISpanFormattable
// probe on a boxed int) into a jump to address 0.
static const pfn c_classlibFunctions[] = {
    &MANAGED_RUNTIME_EXPORT_NAME(GetRuntimeException),
    &MANAGED_RUNTIME_EXPORT_NAME(RuntimeFailFast),
    &MANAGED_RUNTIME_EXPORT_NAME(ThreadEntryPoint),
    &MANAGED_RUNTIME_EXPORT_NAME(AppendExceptionStackFrame),
#if !defined(BFLAT_DOTNET) || BFLAT_DOTNET >= 11
    &MANAGED_RUNTIME_EXPORT_NAME(ResolveDispatch),  // slot 4: net11 dispatch resolver
#else
    nullptr,                                        // slot 4: unused on net10 (stock)
#endif
    &MANAGED_RUNTIME_EXPORT_NAME(GetSystemArrayEEType),
    &MANAGED_RUNTIME_EXPORT_NAME(OnFirstChanceException),
    &MANAGED_RUNTIME_EXPORT_NAME(OnUnhandledException),
};

#ifndef _countof
#define _countof(_array) (sizeof(_array)/sizeof(_array[0]))
#endif

extern "C" void InitializeModules(void* osModule, void ** modules, int count,
    void ** pClasslibFunctions, int nClasslibFunctions);

#define NATIVEAOT_ENTRYPOINT __managed__Main
extern "C" int __managed__Main(int argc, char* argv[]);

/* ACSL++ contract (Frama-Clang dialect; plain Frama-C does not parse C++). */
/*@ // Registers the module and its classlib table with the NativeAOT
    // runtime. The failure branches first store to addresses 1/2 - a
    // deliberate fault whose address marks WHICH init step died in the
    // zkVM trace - so -1 is only reachable if those traps somehow resume.
    // Effects of RhInitialize/RhRegisterOSModule/InitializeModules are
    // runtime-internal and not specifiable here.
    ensures \result == 0 || \result == -1;
*/
extern "C" int
uBootstrap_InitializeRuntime()
{
    if (!RhInitialize(
        /* isDll */ false
        ))
    {
        *(int *)1 = 1;
        return -1;
    }

    void * osModule = PalGetModuleHandleFromPointer(
        (void*)&NATIVEAOT_ENTRYPOINT);

    // TODO: pass struct with parameters instead of the large signature of
    // RhRegisterOSModule
    if (!RhRegisterOSModule(
        osModule,
        (void*)&__managedcode_a, (uint32_t)((char *)&__managedcode_z -
            (char*)&__managedcode_a),
        (void*)&__unbox_a, (uint32_t)((char *)&__unbox_z - (char*)&__unbox_a),
        (void **)&c_classlibFunctions, _countof(c_classlibFunctions)))
    {
        *(int *)2 = 2;
        return -1;
    }

    InitializeModules(osModule, __modules_a, (int)((__modules_z -
        __modules_a)),
    (void **)&c_classlibFunctions, _countof(c_classlibFunctions));

    return 0;
}


/*@ // Host argc/argv are ignored: the managed Main always receives the
    // fake g_bootstrap_argc/argv ("app", single argument). Returns -1 when
    // runtime init fails (if its fault markers resume at all); otherwise
    // the status of __managed__Main, which cannot be constrained here.
    requires g_bootstrap_argc == 1;
    ensures \true;
*/
extern "C" int
uBootstrap_main(int argc, char* argv[])
{
    int ret;

    ret = uBootstrap_InitializeRuntime();
    if (ret != 0)
        return ret;

    return __managed__Main(g_bootstrap_argc, g_bootstrap_argv);
}
