# ExceptionHandler sample

Shows how a guest without unwind tables can take over a `throw` with a **C#
handler that gets the exception object**. The handler prints the exception
and ends the guest cleanly. A guest without unwind tables is a `--remove-eh`
build, or a build for a platform that keeps no unwind tables (Airbender). A
build that keeps the tables dispatches exceptions normally and needs none of
this.

## Background

The JIT lowers a managed `throw` to `CORINFO_HELP_THROW`, which calls
`RhpThrowEx`. When the build has no unwind tables (the `eh: failfast`
condition), the manifest of the `rhp` module adds `--wrap=RhpThrowEx`. The
call then goes to `__wrap_RhpThrowEx` in `rhp/module.c`.

`__wrap_RhpThrowEx` gives the exception object (in `a0`) to a **weak** symbol
`ZkvmThrow`. A program that exports one, with
`[UnmanagedCallersOnly(EntryPoint = "ZkvmThrow")]`, takes over the throw and
gets the live `Exception` reference. A program that does not export it still
links, and the throw fails fast.

```csharp
[UnmanagedCallersOnly(EntryPoint = "ZkvmThrow")]
static void ZkvmThrow(IntPtr exceptionObj)
{
    // The a0 pointer value IS the managed object reference; reinterpret it.
    Exception ex = Unsafe.As<IntPtr, Exception>(ref exceptionObj);
    Print("[ZkvmThrow] " + ex.GetType() + ": " + ex.Message);
    NativeExit(0);
}
```

## Two zkVM rules

1. **Print with `sys_write`, not `Console.WriteLine`.** The `zisk` platform
   has the `no-console` flag, so the .NET console path
   (`SystemNative_Write`, `__stdio_write`) does nothing. `sys_write` is in
   the bindings library of bflat-libziskos (the same call as
   `Nethermind.Zkvm.Abstractions.IO.PrintLine`), and writes to the real
   output of ZisK. Only the full package links it: the platform files alone
   do not have it.

2. **End with the native `exit()`, not `Environment.Exit`.** The `pal`
   module sends `exit` to `zkvm_platform_exit` of the platform, which on ZisK
   is the exit ecall (`a7 = 93`). `Environment.Exit` runs the managed runtime
   shutdown. While an exception is in flight, the shutdown enters the throw
   path again, and the handler recurses forever.

The `rhp` and `pal` modules give the runtime side: the forward in
`__wrap_RhpThrowEx`, the `exit` wrap, and a no-op `RhpReversePInvoke`, so
that the handler can start from the throw path.

## Build and run

```console
$ bflat build exhandler.cs --arch riscv64 --os linux --libc zisk --stdlib dotnet \
      --no-pthread --no-pie --no-stacktrace-data --remove-eh \
      --extlib https://github.com/NethermindEth/bflat-libziskos:v1.4.0-alpha.153
$ ziskemu -e ./exhandler
```

## Expected output

```
before throw
[ZkvmThrow] System.InvalidOperationException: boom from managed code
```

The emulator then ends cleanly: `NativeExit` in the handler gives the ZisK
exit ecall.
