/**
 * @file
 * @brief Simple bootstrap based on .NET bootstrap.
 *
 * Copyright is preserved from dotnet bootstrap (MIT license)
 *
 * @author Maxim Menshikov <maksim.menshikov@nethermind.io>
 */
#include <stdint.h>

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
MANAGED_RUNTIME_EXPORT(AppendExceptionStackFrame)
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

static const pfn c_classlibFunctions[] = {
    &MANAGED_RUNTIME_EXPORT_NAME(GetRuntimeException),
    &MANAGED_RUNTIME_EXPORT_NAME(RuntimeFailFast),
    nullptr, // &UnhandledExceptionHandler,
    &MANAGED_RUNTIME_EXPORT_NAME(AppendExceptionStackFrame),
    nullptr, // &CheckStaticClassConstruction,
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


extern "C" int
uBootstrap_main(int argc, char* argv[])
{
    int ret;

    ret = uBootstrap_InitializeRuntime();
    if (ret != 0)
        return ret;

    return __managed__Main(argc, argv);
}
