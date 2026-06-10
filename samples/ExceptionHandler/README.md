# ExceptionHandler sample

Shows how to replace the default zkVM fail-fast on a managed `throw` with a
**C# handler that receives the exception object**, prints it, and exits cleanly.

## Background

In the Zisk zkVM build a managed `throw` is lowered by the JIT to
`CORINFO_HELP_THROW`, which calls `RhpThrowEx`. bflat redirects that symbol with
`--wrap=RhpThrowEx` to `__wrap_RhpThrowEx` (in `rhp/module.c`). Normally that
wrapper just fail-fasts.

This sample wires the wrapper to forward the exception object (passed in `a0`)
to a **weak** symbol `ZkvmThrow`. A program that exports one — via
`[UnmanagedCallersOnly(EntryPoint = "ZkvmThrow")]` — takes over the throw and
gets the live `Exception` reference. A program that does not export it links
fine and keeps the plain fail-fast (the reference stays null).

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

## Two zkVM-specific rules

1. **Print with `sys_write`, not `Console.WriteLine`.** Under `--libc zisk` the
   .NET console path (`SystemNative_Write` / `__stdio_write`) is wrapped to a
   no-op, so `Console` output is invisible. `sys_write` (from `libziskos`, the
   same call behind `Nethermind.Zkvm.Abstractions.IO.PrintLine`) reaches the
   real zkVM stdout. It is only linked when you pass `--extlib`.

2. **Exit with the native `exit()`, not `Environment.Exit`.** The zkVM only
   ends on a Zisk exit ecall (`a7 = 93`); `pal`'s `__wrap_exit` emits it.
   `Environment.Exit` instead runs the managed runtime shutdown, which — while
   an exception is in flight — re-enters the throw path and recurses through the
   handler forever.

The runtime side (forwarding in `__wrap_RhpThrowEx`, the `exit` override, and a
no-op `RhpReversePInvoke` so the handler can be entered from the throw path) is
provided by the `rhp` / `pal` modules and the bflat linker wiring.

## Build and run

```console
$ bflat build exhandler.cs --os linux --libc zisk --stdlib dotnet \
      --no-pthread --no-pie --no-stacktrace-data \
      --extlib https://github.com/NethermindEth/bflat-libziskos:v1.0.0-preview.27
$ ziskemu -e ./exhandler.patched          # or run inside Zisk
```

## Expected output

```
before throw
[ZkvmThrow] System.InvalidOperationException: boom from managed code
```

The emulator then completes cleanly (the handler's `NativeExit` issues the Zisk
exit ecall).
